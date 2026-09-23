using System.Collections.Generic;
using System.Text.Json;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Devices.SiFi;

namespace MOSAIC.Tests.Components.Devices.SiFi;

/// <summary>
/// Known-answer tests for <see cref="SiFiPacketParser"/>, the static JSON parser for the
/// <c>sifibridge</c> CLI. Every expected value is derived by hand from the packet layout described
/// in the source (channel keys, fixed output order, "latest sample" semantics, guard conditions)
/// and noted on each assertion. Inputs are constructed as literal JSON lines / channel dictionaries
/// so the decode is exercised end to end without any device connection.
/// </summary>
[TestClass]
public class SiFiPacketParserTests
{
    private static Dictionary<string, List<double?>> Data(params (string key, double?[] samples)[] channels)
    {
        var dict = new Dictionary<string, List<double?>>();
        foreach (var (key, samples) in channels)
            dict[key] = new List<double?>(samples);
        return dict;
    }

    // ---------------------------------------------------------------- Classify

    [TestMethod]
    public void Classify_NullLine_ReturnsUnknown()
    {
        // IsNullOrEmpty guard short-circuits before any JSON parse => Unknown, doc left null.
        var kind = SiFiPacketParser.Classify(null!, out var doc);
        doc?.Dispose();

        Assert.AreEqual(SiFiPacketParser.PacketKind.Unknown, kind);
        Assert.IsNull(doc);
    }

    [DataTestMethod]
    [DataRow("")]              // empty => IsNullOrEmpty guard
    [DataRow("not json")]     // not JSON => JsonException caught
    [DataRow("{unclosed")]    // malformed JSON => JsonException caught
    public void Classify_EmptyOrMalformed_ReturnsUnknown(string line)
    {
        // Both the empty guard and the JsonException catch funnel to Unknown (never throws).
        var kind = SiFiPacketParser.Classify(line, out var doc);
        doc?.Dispose();

        Assert.AreEqual(SiFiPacketParser.PacketKind.Unknown, kind);
    }

    [TestMethod]
    public void Classify_ListProperty_ReturnsScanResultsAndParsesDoc()
    {
        // Presence of a top-level "list" property => ScanResults, checked before packet_type.
        var kind = SiFiPacketParser.Classify("{\"list\":{\"devices\":[]}}", out var doc);

        Assert.AreEqual(SiFiPacketParser.PacketKind.ScanResults, kind);
        Assert.IsNotNull(doc); // valid JSON => document handed back for the caller to dispose
        doc!.Dispose();
    }

    [TestMethod]
    public void Classify_ShowProperty_ReturnsShow()
    {
        // Presence of a top-level "show" property => Show.
        var kind = SiFiPacketParser.Classify("{\"show\":\"config\"}", out var doc);
        doc?.Dispose();

        Assert.AreEqual(SiFiPacketParser.PacketKind.Show, kind);
    }

    [DataTestMethod]
    [DataRow("emg_armband", SiFiPacketParser.PacketKind.EmgArmband)]
    [DataRow("emg", SiFiPacketParser.PacketKind.Emg)]
    [DataRow("imu", SiFiPacketParser.PacketKind.Imu)]
    [DataRow("ecg", SiFiPacketParser.PacketKind.Ecg)]
    [DataRow("eda", SiFiPacketParser.PacketKind.Eda)]
    [DataRow("ppg", SiFiPacketParser.PacketKind.Ppg)]
    [DataRow("status", SiFiPacketParser.PacketKind.Status)]
    [DataRow("device_info", SiFiPacketParser.PacketKind.DeviceInfo)]
    public void Classify_KnownPacketType_MapsToMatchingKind(string packetType, SiFiPacketParser.PacketKind expected)
    {
        // The packet_type switch maps each known string 1:1 to its PacketKind.
        var line = $"{{\"packet_type\":\"{packetType}\"}}";

        var kind = SiFiPacketParser.Classify(line, out var doc);
        doc?.Dispose();

        Assert.AreEqual(expected, kind);
    }

    [DataTestMethod]
    [DataRow("start_packet")]  // explicit control packet => ignored
    [DataRow("start_time")]    // explicit control packet => ignored
    [DataRow("something_else")] // unrecognised => default arm
    public void Classify_ControlOrUnknownPacketType_ReturnsUnknown(string packetType)
    {
        // Control packets and any unrecognised packet_type fall through to Unknown.
        var line = $"{{\"packet_type\":\"{packetType}\"}}";

        var kind = SiFiPacketParser.Classify(line, out var doc);
        doc?.Dispose();

        Assert.AreEqual(SiFiPacketParser.PacketKind.Unknown, kind);
    }

    // ---------------------------------------------------------------- ParseChannelData

    [TestMethod]
    public void ParseChannelData_WithDataObject_DeserializesChannelsAndNulls()
    {
        // "data":{"emg0":[1,2],"emg1":[3,null]} => two channels; the JSON null becomes a null entry.
        using var doc = JsonDocument.Parse("{\"data\":{\"emg0\":[1,2],\"emg1\":[3,null]}}");

        var data = SiFiPacketParser.ParseChannelData(doc.RootElement);

        Assert.IsNotNull(data);
        Assert.AreEqual(2, data!.Count);
        CollectionAssert.AreEqual(new double?[] { 1.0, 2.0 }, data["emg0"].ToArray());
        CollectionAssert.AreEqual(new double?[] { 3.0, null }, data["emg1"].ToArray());
    }

    [TestMethod]
    public void ParseChannelData_NoDataProperty_ReturnsNull()
    {
        // Missing "data" property => early null return.
        using var doc = JsonDocument.Parse("{\"packet_type\":\"emg\"}");

        var data = SiFiPacketParser.ParseChannelData(doc.RootElement);

        Assert.IsNull(data);
    }

    // ---------------------------------------------------------------- ExtractEmgArmbandMatrix

    [TestMethod]
    public void ExtractEmgArmbandMatrix_ThreeChannels_BuildsSamplesByChannelsMatrix()
    {
        // Channels sorted ascending -> columns [emg0,emg1,emg2]; rows = samples.
        // emg0=[1,2], emg1=[3,4], emg2=[5,6] => [[1,3,5],[2,4,6]] (2x3).
        var data = Data(("emg0", new double?[] { 1, 2 }),
                        ("emg1", new double?[] { 3, 4 }),
                        ("emg2", new double?[] { 5, 6 }));

        var matrix = SiFiPacketParser.ExtractEmgArmbandMatrix(data);

        Assert.IsNotNull(matrix);
        Assert.AreEqual(2, matrix!.RowCount);
        Assert.AreEqual(3, matrix.ColumnCount);
        Assert.AreEqual(1.0, matrix[0, 0], 1e-9);
        Assert.AreEqual(3.0, matrix[0, 1], 1e-9);
        Assert.AreEqual(5.0, matrix[0, 2], 1e-9);
        Assert.AreEqual(2.0, matrix[1, 0], 1e-9);
        Assert.AreEqual(4.0, matrix[1, 1], 1e-9);
        Assert.AreEqual(6.0, matrix[1, 2], 1e-9);
    }

    [TestMethod]
    public void ExtractEmgArmbandMatrix_RaggedChannels_TruncatesToMinSampleCount()
    {
        // sampleCount = min(3,2) = 2 => only the first two rows survive.
        // emg0=[1,2,3], emg1=[4,5] => [[1,4],[2,5]] (2x2), the trailing "3" is dropped.
        var data = Data(("emg0", new double?[] { 1, 2, 3 }),
                        ("emg1", new double?[] { 4, 5 }));

        var matrix = SiFiPacketParser.ExtractEmgArmbandMatrix(data);

        Assert.IsNotNull(matrix);
        Assert.AreEqual(2, matrix!.RowCount);
        Assert.AreEqual(2, matrix.ColumnCount);
        Assert.AreEqual(1.0, matrix[0, 0], 1e-9);
        Assert.AreEqual(4.0, matrix[0, 1], 1e-9);
        Assert.AreEqual(2.0, matrix[1, 0], 1e-9);
        Assert.AreEqual(5.0, matrix[1, 1], 1e-9);
    }

    [TestMethod]
    public void ExtractEmgArmbandMatrix_NullSample_TreatedAsZero()
    {
        // A null entry is substituted with 0.0. emg0=[1,null] => column0 = [1,0].
        var data = Data(("emg0", new double?[] { 1, null }));

        var matrix = SiFiPacketParser.ExtractEmgArmbandMatrix(data);

        Assert.IsNotNull(matrix);
        Assert.AreEqual(2, matrix!.RowCount);
        Assert.AreEqual(1, matrix.ColumnCount);
        Assert.AreEqual(1.0, matrix[0, 0], 1e-9);
        Assert.AreEqual(0.0, matrix[1, 0], 1e-9); // null => 0.0
    }

    [TestMethod]
    public void ExtractEmgArmbandMatrix_NoEmgChannels_ReturnsNull()
    {
        // No key starts with "emg" => null.
        var data = Data(("ecg", new double?[] { 1, 2 }));

        var matrix = SiFiPacketParser.ExtractEmgArmbandMatrix(data);

        Assert.IsNull(matrix);
    }

    // ---------------------------------------------------------------- ExtractSingleEmg

    [TestMethod]
    public void ExtractSingleEmg_Present_ReturnsAllSamples()
    {
        // "emg" => vector of every sample, in order, nulls => 0.0. [10,null,30] => [10,0,30].
        var data = Data(("emg", new double?[] { 10, null, 30 }));

        var vec = SiFiPacketParser.ExtractSingleEmg(data);

        Assert.IsNotNull(vec);
        Assert.AreEqual(3, vec!.Count);
        Assert.AreEqual(10.0, vec[0], 1e-9);
        Assert.AreEqual(0.0, vec[1], 1e-9); // null => 0.0
        Assert.AreEqual(30.0, vec[2], 1e-9);
    }

    [TestMethod]
    public void ExtractSingleEmg_MissingChannel_ReturnsNull()
    {
        // No "emg" key => null.
        var data = Data(("ecg", new double?[] { 1 }));

        var vec = SiFiPacketParser.ExtractSingleEmg(data);

        Assert.IsNull(vec);
    }

    // ---------------------------------------------------------------- ExtractImuVector

    [TestMethod]
    public void ExtractImuVector_AllChannels_ReturnsLatestSampleInFixedOrder()
    {
        // Order is [ax,ay,az,qw,qx,qy,qz]; each slot takes the LAST sample of its channel.
        // ax=[0.1,0.2] => 0.2 (latest); the rest single-valued.
        var data = Data(("ax", new double?[] { 0.1, 0.2 }),
                        ("ay", new double?[] { 1.0 }),
                        ("az", new double?[] { 2.0 }),
                        ("qw", new double?[] { 3.0 }),
                        ("qx", new double?[] { 4.0 }),
                        ("qy", new double?[] { 5.0 }),
                        ("qz", new double?[] { 6.0 }));

        var vec = SiFiPacketParser.ExtractImuVector(data);

        Assert.IsNotNull(vec);
        Assert.AreEqual(7, vec!.Count);
        Assert.AreEqual(0.2, vec[0], 1e-9); // latest of ax
        Assert.AreEqual(1.0, vec[1], 1e-9);
        Assert.AreEqual(2.0, vec[2], 1e-9);
        Assert.AreEqual(3.0, vec[3], 1e-9);
        Assert.AreEqual(4.0, vec[4], 1e-9);
        Assert.AreEqual(5.0, vec[5], 1e-9);
        Assert.AreEqual(6.0, vec[6], 1e-9);
    }

    [TestMethod]
    public void ExtractImuVector_MissingChannelsFilledWithZero()
    {
        // At least one IMU key present => returns a full 7-vector; absent channels default to 0.0.
        // Only ax=[9] present => [9,0,0,0,0,0,0].
        var data = Data(("ax", new double?[] { 9.0 }));

        var vec = SiFiPacketParser.ExtractImuVector(data);

        Assert.IsNotNull(vec);
        Assert.AreEqual(7, vec!.Count);
        Assert.AreEqual(9.0, vec[0], 1e-9);
        Assert.AreEqual(0.0, vec[1], 1e-9); // az/ay/... absent => 0.0
        Assert.AreEqual(0.0, vec[6], 1e-9);
    }

    [TestMethod]
    public void ExtractImuVector_NoImuChannels_ReturnsNull()
    {
        // None of ax..qz present => null.
        var data = Data(("emg0", new double?[] { 1 }));

        var vec = SiFiPacketParser.ExtractImuVector(data);

        Assert.IsNull(vec);
    }

    // ---------------------------------------------------------------- ExtractPpgVector

    [TestMethod]
    public void ExtractPpgVector_AllChannels_ReturnsLatestSampleInFixedOrder()
    {
        // Order [ir,r,g,b]; each slot = last sample. r=[7,8] => 8 (latest).
        var data = Data(("ir", new double?[] { 100.0 }),
                        ("r", new double?[] { 7.0, 8.0 }),
                        ("g", new double?[] { 200.0 }),
                        ("b", new double?[] { 300.0 }));

        var vec = SiFiPacketParser.ExtractPpgVector(data);

        Assert.IsNotNull(vec);
        Assert.AreEqual(4, vec!.Count);
        Assert.AreEqual(100.0, vec[0], 1e-9);
        Assert.AreEqual(8.0, vec[1], 1e-9);   // latest of r
        Assert.AreEqual(200.0, vec[2], 1e-9);
        Assert.AreEqual(300.0, vec[3], 1e-9);
    }

    [TestMethod]
    public void ExtractPpgVector_NoPpgChannels_ReturnsNull()
    {
        // None of ir,r,g,b present => null.
        var data = Data(("emg", new double?[] { 1 }));

        var vec = SiFiPacketParser.ExtractPpgVector(data);

        Assert.IsNull(vec);
    }

    // ---------------------------------------------------------------- ExtractSingleChannel

    [TestMethod]
    public void ExtractSingleChannel_NamedChannel_ReturnsAllSamples()
    {
        // Returns every sample of the requested channel; null => 0.0. ecg=[1,null,3] => [1,0,3].
        var data = Data(("ecg", new double?[] { 1, null, 3 }));

        var vec = SiFiPacketParser.ExtractSingleChannel(data, "ecg");

        Assert.IsNotNull(vec);
        Assert.AreEqual(3, vec!.Count);
        Assert.AreEqual(1.0, vec[0], 1e-9);
        Assert.AreEqual(0.0, vec[1], 1e-9); // null => 0.0
        Assert.AreEqual(3.0, vec[2], 1e-9);
    }

    [TestMethod]
    public void ExtractSingleChannel_MissingChannel_ReturnsNull()
    {
        // Requested channel absent => null.
        var data = Data(("ecg", new double?[] { 1 }));

        var vec = SiFiPacketParser.ExtractSingleChannel(data, "eda");

        Assert.IsNull(vec);
    }

    // ---------------------------------------------------------------- TryParseBatteryLevel

    [TestMethod]
    public void TryParseBatteryLevel_Present_ReturnsTruncatedLatestValue()
    {
        // battery_% latest = 85.9 => (int)85.9 = 85 (truncation toward zero).
        var data = Data(("battery_%", new double?[] { 90.0, 85.9 }));

        var found = SiFiPacketParser.TryParseBatteryLevel(data, out var level);

        Assert.IsTrue(found);
        Assert.AreEqual(85, level);
    }

    [TestMethod]
    public void TryParseBatteryLevel_Missing_ReturnsFalseWithSentinel()
    {
        // No battery_% key => false, level stays at the -1 sentinel.
        var data = Data(("temperature", new double?[] { 25.0 }));

        var found = SiFiPacketParser.TryParseBatteryLevel(data, out var level);

        Assert.IsFalse(found);
        Assert.AreEqual(-1, level);
    }

    // ---------------------------------------------------------------- TryParseTemperature

    [TestMethod]
    public void TryParseTemperature_Present_ReturnsLatestValue()
    {
        // temperature latest = 26.5.
        var data = Data(("temperature", new double?[] { 25.0, 26.5 }));

        var found = SiFiPacketParser.TryParseTemperature(data, out var temperature);

        Assert.IsTrue(found);
        Assert.AreEqual(26.5, temperature, 1e-9);
    }

    [TestMethod]
    public void TryParseTemperature_Missing_ReturnsFalseWithNaN()
    {
        // No temperature key => false, out defaults to NaN.
        var data = Data(("battery_%", new double?[] { 50.0 }));

        var found = SiFiPacketParser.TryParseTemperature(data, out var temperature);

        Assert.IsFalse(found);
        Assert.IsTrue(double.IsNaN(temperature));
    }

    // ---------------------------------------------------------------- ParseScanResults

    [TestMethod]
    public void ParseScanResults_DevicesWrapper_ReturnsNameAddressPairs()
    {
        // {"devices":[...]} is unwrapped; "id" is the address source.
        using var doc = JsonDocument.Parse(
            "{\"devices\":[{\"name\":\"Band A\",\"id\":\"AA:BB\"},{\"name\":\"Band B\",\"id\":\"CC:DD\"}]}");

        var results = SiFiPacketParser.ParseScanResults(doc.RootElement);

        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("Band A", results[0].Name);
        Assert.AreEqual("AA:BB", results[0].Address);
        Assert.AreEqual("Band B", results[1].Name);
        Assert.AreEqual("CC:DD", results[1].Address);
    }

    [TestMethod]
    public void ParseScanResults_BareArray_WithFallbacksAndFilters()
    {
        // A bare array is accepted directly. Row 1: no name => "Unknown", address from "address".
        // Row 2: address from "mac". Row 3: no address key => filtered out entirely.
        using var doc = JsonDocument.Parse(
            "[{\"address\":\"11:22\"},{\"name\":\"M\",\"mac\":\"33:44\"},{\"name\":\"NoAddr\"}]");

        var results = SiFiPacketParser.ParseScanResults(doc.RootElement);

        Assert.AreEqual(2, results.Count); // the address-less entry is dropped
        Assert.AreEqual("Unknown", results[0].Name); // missing name => default
        Assert.AreEqual("11:22", results[0].Address);
        Assert.AreEqual("M", results[1].Name);
        Assert.AreEqual("33:44", results[1].Address); // "mac" fallback
    }

    // ---------------------------------------------------------------- ExtractSampleRate

    [TestMethod]
    public void ExtractSampleRate_NumericField_ReturnsValue()
    {
        // Numeric sample_rate is returned as a double.
        using var doc = JsonDocument.Parse("{\"sample_rate\":2000}");

        var rate = SiFiPacketParser.ExtractSampleRate(doc.RootElement);

        Assert.IsTrue(rate.HasValue);
        Assert.AreEqual(2000.0, rate!.Value, 1e-9);
    }

    [DataTestMethod]
    [DataRow("{\"packet_type\":\"emg\"}")]   // no sample_rate key
    [DataRow("{\"sample_rate\":\"fast\"}")]  // present but not a number
    public void ExtractSampleRate_MissingOrNonNumeric_ReturnsNull(string json)
    {
        // Absent field, or a non-Number value kind, both yield null.
        using var doc = JsonDocument.Parse(json);

        var rate = SiFiPacketParser.ExtractSampleRate(doc.RootElement);

        Assert.IsNull(rate);
    }
}
