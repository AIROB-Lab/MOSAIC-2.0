using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace MOSAIC.Components.Devices.SiFi;

/// <summary>
/// Static parser for JSON packets emitted by the <c>sifibridge</c> CLI.
/// </summary>
/// <remarks>
/// <para>
/// Each stdout line from the bridge is one JSON object. The <c>packet_type</c> field
/// determines what kind of data is in the <c>data</c> object. Each packet contains
/// samples for <b>one sensor type only</b> — EMG and IMU arrive as separate packets.
/// </para>
/// <para>
/// <b>Packet types:</b>
/// <list type="table">
///   <listheader><term>packet_type</term><description>Channels</description></listheader>
///   <item><term>emg_armband</term><description>emg0–emg7</description></item>
///   <item><term>emg</term><description>emg (single channel)</description></item>
///   <item><term>imu</term><description>ax, ay, az, qw, qx, qy, qz</description></item>
///   <item><term>ecg</term><description>ecg</description></item>
///   <item><term>eda</term><description>eda</description></item>
///   <item><term>ppg</term><description>ir, r, g, b</description></item>
///   <item><term>status</term><description>battery_%, temperature</description></item>
///   <item><term>device_info</term><description>(device metadata)</description></item>
/// </list>
/// </para>
/// </remarks>
public static class SiFiPacketParser
{
    /// <summary>The type of packet parsed from a bridge JSON line.</summary>
    public enum PacketKind
    {
        /// <summary>Unrecognised or non-JSON output.</summary>
        Unknown,
        /// <summary>8-channel EMG armband data (emg0–emg7).</summary>
        EmgArmband,
        /// <summary>Single-channel EMG data.</summary>
        Emg,
        /// <summary>IMU data (accelerometer + quaternion).</summary>
        Imu,
        /// <summary>ECG data.</summary>
        Ecg,
        /// <summary>EDA data.</summary>
        Eda,
        /// <summary>PPG data (ir, r, g, b).</summary>
        Ppg,
        /// <summary>Device status (battery, temperature).</summary>
        Status,
        /// <summary>Device info packet.</summary>
        DeviceInfo,
        /// <summary>BLE scan result list.</summary>
        ScanResults,
        /// <summary>Show command response.</summary>
        Show
    }

    /// <summary>Known IMU channel keys in fixed output order.</summary>
    private static readonly string[] ImuKeys = ["ax", "ay", "az", "qw", "qx", "qy", "qz"];

    /// <summary>Known PPG channel keys in fixed output order.</summary>
    private static readonly string[] PpgKeys = ["ir", "r", "g", "b"];

    /// <summary>
    /// Attempts to parse a raw stdout line and determine its packet type.
    /// </summary>
    /// <param name="line">A single stdout line from the bridge.</param>
    /// <param name="doc">
    /// The parsed <see cref="JsonDocument"/> if the line is valid JSON.
    /// Caller must dispose this when done.
    /// </param>
    /// <returns>The <see cref="PacketKind"/>.</returns>
    public static PacketKind Classify(string line, out JsonDocument? doc)
    {
        doc = null;
        if (string.IsNullOrEmpty(line)) return PacketKind.Unknown;

        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return PacketKind.Unknown; }

        var root = doc.RootElement;

        // Non-data responses (scan, show)
        if (root.TryGetProperty("list", out _)) return PacketKind.ScanResults;
        if (root.TryGetProperty("show", out _)) return PacketKind.Show;

        if (root.TryGetProperty("packet_type", out var ptElement))
        {
            var pt = ptElement.GetString();
            return pt switch
            {
                "emg_armband" => PacketKind.EmgArmband,
                "emg"         => PacketKind.Emg,
                "imu"         => PacketKind.Imu,
                "ecg"         => PacketKind.Ecg,
                "eda"         => PacketKind.Eda,
                "ppg"         => PacketKind.Ppg,
                "status"      => PacketKind.Status,
                "device_info" => PacketKind.DeviceInfo,
                "start_packet" or "start_time" => PacketKind.Unknown, // control packets, ignore
                _             => PacketKind.Unknown
            };
        }

        return PacketKind.Unknown;
    }

    /// <summary>
    /// Parses the <c>"data"</c> field from a packet into a channel dictionary.
    /// </summary>
    /// <param name="root">The root JSON element of the packet.</param>
    /// <returns>Channel name → sample list dictionary, or <see langword="null"/> on failure.</returns>
    public static Dictionary<string, List<double?>>? ParseChannelData(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataElement))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<double?>>>(dataElement.GetRawText());
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[SifiParser] Channel data parse error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extracts 8-channel EMG armband data (emg0–emg7) into a samples × channels matrix.
    /// </summary>
    /// <param name="data">Channel dictionary from <see cref="ParseChannelData"/>.</param>
    /// <returns>
    /// A matrix of shape (samples × 8), or <see langword="null"/> if no emg channels found.
    /// </returns>
    public static Matrix<double>? ExtractEmgArmbandMatrix(Dictionary<string, List<double?>> data)
    {
        var channels = data.Keys
            .Where(k => k.StartsWith("emg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k)
            .ToList();

        if (channels.Count == 0) return null;

        int sampleCount = channels.Min(ch => data[ch]?.Count ?? 0);
        if (sampleCount == 0) return null;

        var matrix = DenseMatrix.Create(sampleCount, channels.Count, 0.0);
        for (int c = 0; c < channels.Count; c++)
        {
            var channelData = data[channels[c]];
            for (int s = 0; s < sampleCount; s++)
                matrix[s, c] = channelData?[s] ?? 0.0;
        }

        return matrix;
    }

    /// <summary>
    /// Extracts single-channel EMG data into a column vector.
    /// </summary>
    public static Vector<double>? ExtractSingleEmg(Dictionary<string, List<double?>> data)
    {
        if (!data.TryGetValue("emg", out var samples) || samples is null || samples.Count == 0)
            return null;

        var arr = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            arr[i] = samples[i] ?? 0.0;

        return DenseVector.OfArray(arr);
    }

    /// <summary>
    /// Extracts IMU data into a 7-element vector: [ax, ay, az, qw, qx, qy, qz].
    /// Takes the latest sample from each channel.
    /// </summary>
    public static Vector<double>? ExtractImuVector(Dictionary<string, List<double?>> data)
    {
        bool anyPresent = false;
        for (int i = 0; i < ImuKeys.Length; i++)
        {
            if (data.ContainsKey(ImuKeys[i])) { anyPresent = true; break; }
        }
        if (!anyPresent) return null;

        var vec = new double[ImuKeys.Length];
        for (int i = 0; i < ImuKeys.Length; i++)
        {
            if (data.TryGetValue(ImuKeys[i], out var values) && values is { Count: > 0 })
                vec[i] = values[^1] ?? 0.0;
        }

        return DenseVector.OfArray(vec);
    }

    /// <summary>
    /// Extracts PPG data into a 4-element vector: [ir, r, g, b].
    /// Takes the latest sample from each channel.
    /// </summary>
    public static Vector<double>? ExtractPpgVector(Dictionary<string, List<double?>> data)
    {
        bool anyPresent = false;
        for (int i = 0; i < PpgKeys.Length; i++)
        {
            if (data.ContainsKey(PpgKeys[i])) { anyPresent = true; break; }
        }
        if (!anyPresent) return null;

        var vec = new double[PpgKeys.Length];
        for (int i = 0; i < PpgKeys.Length; i++)
        {
            if (data.TryGetValue(PpgKeys[i], out var values) && values is { Count: > 0 })
                vec[i] = values[^1] ?? 0.0;
        }

        return DenseVector.OfArray(vec);
    }

    /// <summary>
    /// Extracts a single-channel sensor (ECG or EDA) into a vector of all samples.
    /// </summary>
    /// <param name="data">Channel dictionary.</param>
    /// <param name="channelName">Channel key (e.g. <c>"ecg"</c> or <c>"eda"</c>).</param>
    public static Vector<double>? ExtractSingleChannel(Dictionary<string, List<double?>> data, string channelName)
    {
        if (!data.TryGetValue(channelName, out var samples) || samples is null || samples.Count == 0)
            return null;

        var arr = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            arr[i] = samples[i] ?? 0.0;

        return DenseVector.OfArray(arr);
    }

    /// <summary>
    /// Extracts battery level from a status packet's <c>data</c> dictionary.
    /// </summary>
    /// <param name="data">Channel dictionary from a <c>status</c> packet.</param>
    /// <param name="level">Battery percentage if found.</param>
    /// <returns><see langword="true"/> if battery was found.</returns>
    public static bool TryParseBatteryLevel(Dictionary<string, List<double?>> data, out int level)
    {
        level = -1;

        if (data.TryGetValue("battery_%", out var values) && values is { Count: > 0 } && values[^1].HasValue)
        {
            level = (int)values[^1]!.Value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts temperature from a status packet's <c>data</c> dictionary.
    /// </summary>
    public static bool TryParseTemperature(Dictionary<string, List<double?>> data, out double temperature)
    {
        temperature = double.NaN;

        if (data.TryGetValue("temperature", out var values) && values is { Count: > 0 } && values[^1].HasValue)
        {
            temperature = values[^1]!.Value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses BLE scan results from a <c>"list"</c> response.
    /// </summary>
    /// <remarks>
    /// The bridge returns <c>{"list":{"devices":[{"name":"...","id":"MAC"},…]}}</c>.
    /// </remarks>
    public static IReadOnlyList<(string Name, string Address)> ParseScanResults(JsonElement listElement)
    {
        var devices = new List<(string, string)>();

        try
        {
            // Bridge returns {"list":{"devices":[...]}} — unwrap
            JsonElement array;
            if (listElement.ValueKind == JsonValueKind.Object &&
                listElement.TryGetProperty("devices", out var devArray))
                array = devArray;
            else if (listElement.ValueKind == JsonValueKind.Array)
                array = listElement;
            else
                return devices;

            foreach (var item in array.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown";
                var addr = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" :
                           item.TryGetProperty("address", out var a) ? a.GetString() ?? "" :
                           item.TryGetProperty("mac", out var m) ? m.GetString() ?? "" : "";

                if (!string.IsNullOrEmpty(addr))
                    devices.Add((name, addr));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SifiParser] Scan parse error: {ex.Message}");
        }

        return devices;
    }

    /// <summary>
    /// Extracts the sample rate from a data packet, if present.
    /// </summary>
    public static double? ExtractSampleRate(JsonElement root)
    {
        if (root.TryGetProperty("sample_rate", out var sr) && sr.ValueKind == JsonValueKind.Number)
            return sr.GetDouble();
        return null;
    }
}