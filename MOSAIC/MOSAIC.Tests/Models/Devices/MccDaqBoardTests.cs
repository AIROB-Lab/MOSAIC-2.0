using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Models.Devices;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.Tests.Models.Devices;

/// <summary>
/// Tests for <see cref="MccDaqBoard"/> — the Measurement Computing acquisition block ported from
/// iM-Blocks.
/// </summary>
/// <remarks>
/// <para>
/// No board is touched, and no Universal Library call is made. What is covered is the two pieces
/// that sit outside the vendor guard because a wrong answer from either corrupts the signal
/// silently: <see cref="MccDaqBoard.TryLocatePacket"/>, which decides where in the driver's ring
/// buffer the newest packet begins, and <see cref="MccDaqBoard.BuildOutput"/>, which de-interleaves
/// it. Plus the JSON round-trip and the graph constraints.
/// </para>
/// <para>
/// The vendor call layer itself — buffer allocation, <c>AInScan</c>, <c>GetStatus</c>, the
/// count-to-volts scale — needs the SDK installed and a board attached.
/// </para>
/// </remarks>
[TestClass]
public class MccDaqBoardTests
{
    private static IServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    private static MccDaqBoard Board(int lowChannel, int highChannel, int scansPerPacket) =>
        new("Daq", 100) { LowChannel = lowChannel, HighChannel = highChannel, ScansPerPacket = scansPerPacket };

    // ---------------------------------------------------------------- Packet location

    [TestMethod]
    public void TryLocatePacket_BeforeAWholePacketIsWritten_FindsNothing()
    {
        Assert.IsFalse(MccDaqBoard.TryLocatePacket(0, 4, 8, 32, out _, out _));
        Assert.IsFalse(MccDaqBoard.TryLocatePacket(7, 4, 8, 32, out _, out _),
                       "One sample short of the 8-sample packet.");
        Assert.IsTrue(MccDaqBoard.TryLocatePacket(8, 4, 8, 32, out _, out _));
    }

    [TestMethod]
    public void TryLocatePacket_TakesThePacketEndingAtTheWriteCursor()
    {
        // 32 samples written into a 32-sample buffer: the newest 8 are at offset 24.
        Assert.IsTrue(MccDaqBoard.TryLocatePacket(32, 4, 8, 32, out int start, out int first));

        Assert.AreEqual(24, start);
        Assert.AreEqual(8, first, "Fits before the end of the buffer, so there is no second run.");
    }

    [TestMethod]
    public void TryLocatePacket_WhenTheWriterHasLappedTheBuffer_StillTakesTheNewest()
    {
        // 200 samples through a 16-sample buffer. Newest packet is globals 196..199 -> offset 4.
        Assert.IsTrue(MccDaqBoard.TryLocatePacket(200, 2, 4, 16, out int start, out int first));

        Assert.AreEqual(4, start);
        Assert.AreEqual(4, first);
    }

    [TestMethod]
    public void TryLocatePacket_WhenThePacketStraddlesTheWrap_SplitsItIntoTwoRuns()
    {
        // 34 written, 16-sample buffer, 4-sample packet -> newest packet starts at 14.
        Assert.IsTrue(MccDaqBoard.TryLocatePacket(34, 2, 4, 16, out int start, out int first));

        Assert.AreEqual(14, start);
        Assert.AreEqual(2, first, "Only two samples remain before the end of the buffer.");
        // The caller reads the remaining 4 - 2 = 2 samples from offset 0.
    }

    [TestMethod]
    public void TryLocatePacket_FromAMidScanTotal_StaysOnAScanBoundary()
    {
        // 203 is not a multiple of 5: the driver's counter is sitting three samples into a scan.
        // Taking it at face value would rotate every channel in the output by two positions.
        Assert.IsTrue(MccDaqBoard.TryLocatePacket(203, 5, 10, 20, out int start, out _));

        Assert.AreEqual(0, start % 5, "The packet must begin on a scan boundary.");
        Assert.AreEqual(10, start, "Aligned down from 193 to 190, which is offset 10 in a 20-sample buffer.");
    }

    [DataTestMethod]
    [DataRow(0, 8, 32)]     // no channels
    [DataRow(4, 0, 32)]     // no packet
    [DataRow(4, 8, 4)]      // buffer smaller than a packet
    public void TryLocatePacket_RejectsImpossibleGeometry(int channelCount, int packetSamples, int bufferSamples)
    {
        Assert.IsFalse(MccDaqBoard.TryLocatePacket(1000, channelCount, packetSamples, bufferSamples, out _, out _));
    }

    // ---------------------------------------------------------------- Output shape

    [TestMethod]
    public void BuildOutput_OneScanPerPacket_PublishesAVectorOfChannels()
    {
        var block = Board(lowChannel: 0, highChannel: 3, scansPerPacket: 1);

        var output = block.BuildOutput([10, 20, 30, 40]);

        var vector = (Vector)output;
        CollectionAssert.AreEqual(new[] { 10.0, 20.0, 30.0, 40.0 }, vector.ToArray());

        block.Dispose();
    }

    [TestMethod]
    public void BuildOutput_ManyScansPerPacket_PublishesRowsOfScansAndColumnsOfChannels()
    {
        var block = Board(lowChannel: 0, highChannel: 1, scansPerPacket: 3);

        // Interleaved as the Universal Library delivers it: ch0,ch1, ch0,ch1, ch0,ch1.
        var matrix = (Matrix<double>)block.BuildOutput([1, 2, 3, 4, 5, 6]);

        // The iM-Blocks driver published this as one flat six-element vector, so downstream saw
        // six channels that changed identity every other element.
        Assert.AreEqual(3, matrix.RowCount, "Rows are scans.");
        Assert.AreEqual(2, matrix.ColumnCount, "Columns are channels.");
        CollectionAssert.AreEqual(new[] { 1.0, 3.0, 5.0 }, matrix.Column(0).ToArray());
        CollectionAssert.AreEqual(new[] { 2.0, 4.0, 6.0 }, matrix.Column(1).ToArray());

        block.Dispose();
    }

    [TestMethod]
    public void BuildOutput_BeforeConnecting_LeavesCountsUnscaled()
    {
        // The scale is derived from the driver at Connect; until then counts pass through, which
        // is what makes these tests meaningful without a board.
        var block = Board(lowChannel: 0, highChannel: 1, scansPerPacket: 1);

        var vector = (Vector)block.BuildOutput([4095, 0]);

        Assert.AreEqual(4095.0, vector[0], 1e-12);
        Assert.AreEqual(0.0, vector[1], 1e-12);

        block.Dispose();
    }

    // ---------------------------------------------------------------- Channels

    [DataTestMethod]
    [DataRow("0;1;2;3", 0, 3)]
    [DataRow("4;5;6;7", 4, 7)]
    [DataRow("0,1,2,3", 0, 3)]
    [DataRow("3;1;2", 1, 3)]          // order does not matter, only the endpoints
    [DataRow("2;3;5", 2, 5)]          // a gap widens the sweep; 4 is acquired too
    [DataRow(" 0 ; 7 ", 0, 7)]
    public void SetChannels_TakesTheEndpointsOfTheList(string list, int expectedLow, int expectedHigh)
    {
        var block = Board(lowChannel: 0, highChannel: 0, scansPerPacket: 1);

        block.SetChannels(list);

        // The iM-Blocks driver scanned 0..count-1 regardless, so "2;3;5" acquired channels 0,1,2.
        Assert.AreEqual(expectedLow, block.LowChannel);
        Assert.AreEqual(expectedHigh, block.HighChannel);

        block.Dispose();
    }

    [TestMethod]
    public void SetChannels_IgnoresUnparseableInputRatherThanThrowing()
    {
        var block = Board(lowChannel: 1, highChannel: 6, scansPerPacket: 1);

        block.SetChannels("not a channel list");

        Assert.AreEqual(1, block.LowChannel, "A bad list must leave a working configuration alone.");
        Assert.AreEqual(6, block.HighChannel);

        block.Dispose();
    }

    [TestMethod]
    public void ChannelCount_CoversTheWholeSweptRange()
    {
        var block = Board(lowChannel: 2, highChannel: 5, scansPerPacket: 4);

        Assert.AreEqual(4, block.ChannelCount);
        Assert.AreEqual(16, block.PacketSamples);

        block.Dispose();
    }

    // ---------------------------------------------------------------- Rate

    [TestMethod]
    public void RequestedScanRate_IsTickRateTimesScansPerPacket_NotTimesChannels()
    {
        var block = Board(lowChannel: 0, highChannel: 7, scansPerPacket: 10);

        // The iM-Blocks driver multiplied by the channel count a second time and asked the board
        // for 8000 scans/s to feed a pipeline consuming 1000.
        Assert.AreEqual(1000.0, block.RequestedScanRate, 1e-9);

        block.Dispose();
    }

    [TestMethod]
    public void Connect_WithoutAClockRate_RefusesRatherThanGuessing()
    {
        var block = new MccDaqBoard("Daq");   // no rate, no clock attached

        block.Connect();

        Assert.IsFalse(block.IsConnected);
        StringAssert.Contains(block.ConnectionStatus, "clock");

        block.Dispose();
    }

    // ---------------------------------------------------------------- JSON round trip

    [TestMethod]
    public void ConfigureInput_ReadsTheImBlocksParameterOrder()
    {
        var model = new JsonModel { Type = "MccDaq", Name = "Daq", Params = ["2;3;5", 20] };

        var block = MccDaqBoard.ConfigureInput(Services(), model);

        Assert.AreEqual(2, block.LowChannel);
        Assert.AreEqual(5, block.HighChannel);
        Assert.AreEqual(20, block.ScansPerPacket);
        Assert.AreEqual(0, block.BoardNumber, "Defaults fill in for parameters iM-Blocks did not have.");
        Assert.AreEqual("Bip10Volts", block.RangeName);
        Assert.IsFalse(block.UseLegacyScale, "New pipelines get volts unless they ask otherwise.");

        block.Dispose();
    }

    [TestMethod]
    public void ConfigureInput_ReadsTheFullParameterList()
    {
        var model = new JsonModel
        {
            Type = "MccDaq", Name = "Daq", Params = ["0;3", 5, 2, "Bip5Volts", "legacy"]
        };

        var block = MccDaqBoard.ConfigureInput(Services(), model);

        Assert.AreEqual(0, block.LowChannel);
        Assert.AreEqual(3, block.HighChannel);
        Assert.AreEqual(5, block.ScansPerPacket);
        Assert.AreEqual(2, block.BoardNumber);
        Assert.AreEqual("Bip5Volts", block.RangeName);
        Assert.IsTrue(block.UseLegacyScale);

        block.Dispose();
    }

    [TestMethod]
    public void ToJsonModel_RoundTripsThroughConfigureInput()
    {
        var original = new MccDaqBoard("Daq", 100)
        {
            LowChannel = 1, HighChannel = 5, ScansPerPacket = 7,
            BoardNumber = 3, RangeName = "Bip5Volts", UseLegacyScale = true
        };

        var restored = MccDaqBoard.ConfigureInput(Services(), original.ToJsonModel());

        Assert.AreEqual(original.LowChannel, restored.LowChannel);
        Assert.AreEqual(original.HighChannel, restored.HighChannel);
        Assert.AreEqual(original.ScansPerPacket, restored.ScansPerPacket);
        Assert.AreEqual(original.BoardNumber, restored.BoardNumber);
        Assert.AreEqual(original.RangeName, restored.RangeName);
        Assert.AreEqual(original.UseLegacyScale, restored.UseLegacyScale);

        original.Dispose();
        restored.Dispose();
    }

    // ---------------------------------------------------------------- Graph constraints

    [TestMethod]
    public void Constraints_RequireExactlyOneClockInput()
    {
        var block = new MccDaqBoard("Daq", 100);

        Assert.AreEqual(1, block.MinInputs);
        Assert.AreEqual(1, block.MaxInputs);
        CollectionAssert.AreEqual(new[] { "ClockBlock" }, block.AllowableBlocks);

        block.Dispose();
    }

    [TestMethod]
    public void KnownRanges_AreOfferedForTheCardPicker()
    {
        CollectionAssert.Contains(MccDaqBoard.KnownRanges, "Bip10Volts");
        CollectionAssert.Contains(MccDaqBoard.KnownRanges, "Uni5Volts");
    }
}
