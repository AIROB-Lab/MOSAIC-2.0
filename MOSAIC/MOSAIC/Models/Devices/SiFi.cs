using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Devices.SiFi;
using MOSAIC.Visualization;
using MOSAIC.Visualization.ScopeMonitor;
using static MOSAIC.Components.Basics.JsonModel;

namespace MOSAIC.Models.Devices;

/// <summary>
/// Source block for SiFi Labs BLE biosignal devices (BioArmband / BioPoint).
/// </summary>
/// <remarks>
/// <para>
/// Each sensor type gets its own <see cref="ScopeMonitor"/> for independent visualization.
/// Only sensors included in <see cref="SiFi.EnabledSensors"/> are configured on the device;
/// their scopes are created lazily on first data arrival.
/// </para>
/// </remarks>
/// <example>
/// <para>Block entry for a larger pipeline. Params: device MAC address, bridge executable, mains notch mode, lower and upper bandpass cutoffs in Hz. Replace the example MAC address with your paired device address.</para>
/// <code language="json">
/// {
///   "SiFi": {
///     "Type": "sifi",
///     "Inputs": [],
///     "Params": ["AA:BB:CC:DD:EE:FF", "sifibridge", "on50", 20, 450]
///   }
/// }
/// </code>
/// </example>
public partial class SiFi : BaseBlock
{
    /// <summary>Source block — produces a stream and takes no inputs.</summary>
    public override int MinInputs => 0;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 0;

    #region Enums

    /// <summary>Mains notch filter options matching the sifibridge CLI.</summary>
    public enum MainsNotchMode { Off, On50, On60 }

    /// <summary>Sensor enable flags for configuration.</summary>
    [Flags]
    public enum SensorFlags
    {
        None = 0, Emg = 1, Imu = 2, Eda = 4, Ppg = 8, Ecg = 16
    }

    #endregion

    #region Fields

    private readonly SifiBridge _bridge;
    private string? _resolvedMac;
    private readonly Dictionary<string, string> _displayToMac = new();
    private bool _verboseLogging = true;
    private bool _disposed;

    #endregion

    #region Properties

    /// <summary>Full visualization bundle for EMG (scope + spider + heatmap).</summary>
    public BlockVisualization Viz { get; set; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>Lightweight scope for IMU (7ch: ax,ay,az,qw,qx,qy,qz).</summary>
    public ScopeMonitor ScopeImu { get; } = new();

    /// <summary>Lightweight scope for ECG (single channel).</summary>
    public ScopeMonitor ScopeEcg { get; } = new();

    /// <summary>Lightweight scope for EDA (single channel).</summary>
    public ScopeMonitor ScopeEda { get; } = new();

    /// <summary>Lightweight scope for PPG (4ch: ir,r,g,b).</summary>
    public ScopeMonitor ScopePpg { get; } = new();

    /// <summary>BLE MAC address from the JSON config.</summary>
    public string MacAddress { get; }

    /// <summary>Mains notch filter mode.</summary>
    public MainsNotchMode MainsNotch { get; }

    /// <summary>EMG bandpass low cutoff in Hz.</summary>
    public double BandpassLow { get; }

    /// <summary>EMG bandpass high cutoff in Hz.</summary>
    public double BandpassHigh { get; }

    /// <summary>Which sensors are enabled. Set before calling connect.</summary>
    [ObservableProperty] private SensorFlags _enabledSensors = SensorFlags.Emg;

    /// <summary>Whether the device is currently streaming.</summary>
    [ObservableProperty] private bool _isStreaming;

    /// <summary>Whether the device is connected.</summary>
    [ObservableProperty] private bool _isConnected;

    /// <summary>Human-readable status.</summary>
    [ObservableProperty] private string _deviceStatus = "Disconnected";

    /// <summary>Battery level (0–100), or -1 if unknown.</summary>
    [ObservableProperty] private int _batteryLevel = -1;

    /// <summary>Device temperature in °C, or NaN if unknown.</summary>
    [ObservableProperty] private double _temperature = double.NaN;

    /// <summary>Raw device info from <c>show</c>.</summary>
    [ObservableProperty] private string _deviceInfo = "";

    /// <summary>Total data packets received.</summary>
    public long PacketsReceived { get; private set; }

    /// <summary>Total EMG sample rows published.</summary>
    public long SamplesPublished { get; private set; }

    /// <summary>Number of EMG channels detected.</summary>
    public int EmgChannelCount { get; private set; }

    /// <summary>Discovered BLE devices for the UI picker.</summary>
    public ObservableCollection<string> FoundDevices { get; } = new();

    #endregion

    #region Constructor

    public SiFi(string name, double desiredRate, string macAddress,
                string bridgePath = "sifibridge",
                MainsNotchMode mainsNotch = MainsNotchMode.On50,
                double bandpassLow = 20, double bandpassHigh = 450)
        : base(name, desiredRate)
    {
        MacAddress = macAddress ?? throw new ArgumentNullException(nameof(macAddress));
        MainsNotch = mainsNotch;
        BandpassLow = bandpassLow;
        BandpassHigh = bandpassHigh;

        SignalRate = 1500;

        _bridge = new SifiBridge(bridgePath);
        _bridge.OutputReceived += OnBridgeOutput;
        _bridge.ErrorReceived += line => Debug.WriteLine($"[{Name}] [ERR] {line}");
        _bridge.Exited += () =>
        {
            IsStreaming = false;
            IsConnected = false;
            DeviceStatus = "Bridge exited";
        };
    }

    #endregion

    #region ConfigureInput / JSON Export

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_emg.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_emg";

    public static SiFi ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "Sifi";
        var rate = m.DesiredRate ?? 500;
        var p = m.Params;

        if (p is null || p.Count < 1 || string.IsNullOrWhiteSpace(GetString(p[0], null)))
            throw new ArgumentException($"Sifi '{name}' requires params: [macAddress].");

        var mac = GetString(p[0], null)!;
        var bridge = p?.Count > 1 ? GetString(p[1], "sifibridge") ?? "sifibridge" : "sifibridge";
        var mainsStr = p?.Count > 2 ? GetString(p[2], "on50") ?? "on50" : "on50";
        if (!Enum.TryParse<MainsNotchMode>(mainsStr, ignoreCase: true, out var mainsNotch))
            mainsNotch = MainsNotchMode.On50;
        var bpLow = p?.Count > 3 ? GetDouble(p[3], 20) : 20;
        var bpHigh = p?.Count > 4 ? GetDouble(p[4], 450) : 450;

        var block = ActivatorUtilities.CreateInstance<SiFi>(
            sp, name, rate, mac, bridge, mainsNotch, bpLow, bpHigh);
        return block;
    }

    protected override string JsonTypeName => "Sifi";

    protected override IReadOnlyList<object>? GetJsonParams()
    {
        var mac = _resolvedMac ?? MacAddress;
        var list = new List<object> { mac };

        var hasNonDefaultMains = MainsNotch != MainsNotchMode.On50;
        var hasNonDefaultBandpass = Math.Abs(BandpassLow - 20) > 0.1 || Math.Abs(BandpassHigh - 450) > 0.1;

        if (hasNonDefaultMains || hasNonDefaultBandpass)
        {
            list.Add("sifibridge");
            list.Add(MainsNotch.ToString().ToLowerInvariant());
        }
        if (hasNonDefaultBandpass)
        {
            list.Add(BandpassLow);
            list.Add(BandpassHigh);
        }

        return list;
    }

    #endregion

    #region Scan / Connect / Disconnect

    public async Task ScanAsync(CancellationToken ct = default)
    {
        _bridge.EnsureRunning();
        DeviceStatus = "Scanning...";

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        { FoundDevices.Clear(); _displayToMac.Clear(); }
        else
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            { FoundDevices.Clear(); _displayToMac.Clear(); });

        await _bridge.SendAsync("list ble");
        await Task.Delay(5000, ct);

        DeviceStatus = FoundDevices.Count > 0
            ? $"Found {FoundDevices.Count} device(s)"
            : "No devices found";
    }

    public async Task<bool> ConnectAsync(string selectedDisplayName, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(selectedDisplayName)) return false;

        _resolvedMac = _displayToMac.TryGetValue(selectedDisplayName, out var mac) ? mac : selectedDisplayName;

        _bridge.EnsureRunning();
        DeviceStatus = "Connecting...";
        Debug.WriteLine($"[{Name}] Connecting to {_resolvedMac}");

        await _bridge.SendAsync($"connect {_resolvedMac}");
        await Task.Delay(3000, ct);

        IsConnected = true;
        DeviceStatus = "Connected";

        await _bridge.SendAsync("show");
        await Task.Delay(300, ct);

        // Configure sensors based on EnabledSensors flags
        var f = EnabledSensors;
        await _bridge.SendAsync(
            $"configure sensors" +
            $" --emg {Flag(f, SensorFlags.Emg)}" +
            $" --imu {Flag(f, SensorFlags.Imu)}" +
            $" --eda {Flag(f, SensorFlags.Eda)}" +
            $" --ppg {Flag(f, SensorFlags.Ppg)}" +
            $" --ecg {Flag(f, SensorFlags.Ecg)}");
        await Task.Delay(200, ct);

        // Configure EMG filters (only matters if EMG is enabled)
        if (f.HasFlag(SensorFlags.Emg))
        {
            var mainsArg = MainsNotch switch
            {
                MainsNotchMode.On50 => "on50",
                MainsNotchMode.On60 => "on60",
                _ => "off"
            };
            await _bridge.SendAsync(
                $"configure emg --state on --dc-notch on --mains-notch {mainsArg} " +
                $"--bandpass on --flo {BandpassLow:F0} --fhi {BandpassHigh:F0}");
            await Task.Delay(200, ct);
        }

        await _bridge.SendAsync("start");
        IsStreaming = true;
        DeviceStatus = "Streaming";
        Debug.WriteLine($"[{Name}] Streaming started (sensors: {f})");
        return true;
    }

    public async Task<bool> ConnectWithConfigMacAsync(CancellationToken ct = default)
        => await ConnectAsync(MacAddress, ct);

    public async Task DisconnectAsync()
    {
        if (!_bridge.IsRunning) return;

        IsStreaming = false;
        DeviceStatus = "Stopping...";

        await _bridge.SendAsync("stop");
        await Task.Delay(200);
        await _bridge.SendAsync("disconnect");
        await Task.Delay(200);

        IsConnected = false;
        DeviceStatus = "Disconnected";
    }

    public async Task RequestStatusAsync()
    {
        if (!_bridge.IsRunning) return;
        await _bridge.SendAsync("command get-device-info");
        await _bridge.SendAsync("command start-status-update");
    }

    #endregion

    #region Bridge Output Routing

    private void OnBridgeOutput(string line)
    {
        if (_disposed) return;

        if (_verboseLogging)
            Debug.WriteLine($"[{Name}] << {line}");

        var kind = SiFiPacketParser.Classify(line, out var doc);
        if (doc is null) return;

        using (doc)
        {
            var root = doc.RootElement;

            switch (kind)
            {
                case SiFiPacketParser.PacketKind.EmgArmband:
                    _verboseLogging = false;
                    HandleEmgArmband(root);
                    break;

                case SiFiPacketParser.PacketKind.Emg:
                    _verboseLogging = false;
                    HandleSingleEmg(root);
                    break;

                case SiFiPacketParser.PacketKind.Imu:
                    HandleImu(root);
                    break;

                case SiFiPacketParser.PacketKind.Ecg:
                    HandleTaggedSensor(root, "ecg", "ecg", ScopeEcg);
                    break;

                case SiFiPacketParser.PacketKind.Eda:
                    HandleTaggedSensor(root, "eda", "eda", ScopeEda);
                    break;

                case SiFiPacketParser.PacketKind.Ppg:
                    HandlePpg(root);
                    break;

                case SiFiPacketParser.PacketKind.Status:
                    HandleStatus(root);
                    break;

                case SiFiPacketParser.PacketKind.DeviceInfo:
                    HandleDeviceInfoPacket(root);
                    break;

                case SiFiPacketParser.PacketKind.ScanResults:
                    HandleScanResults(root.GetProperty("list"));
                    break;

                case SiFiPacketParser.PacketKind.Show:
                    HandleShow(root.GetProperty("show"));
                    break;
            }
        }
    }

    #endregion

    #region Packet Handlers

    private void HandleEmgArmband(JsonElement root)
    {
        var data = SiFiPacketParser.ParseChannelData(root);
        if (data is null) return;

        PacketsReceived++;

        // Try armband (emg0–emg7) first, fall back to single channel
        var emg = SiFiPacketParser.ExtractEmgArmbandMatrix(data);
        if (emg is not null && emg.RowCount > 0)
        {
            SamplesPublished += emg.RowCount;
            if (EmgChannelCount == 0)
            {
                EmgChannelCount = emg.ColumnCount;
                Debug.WriteLine($"[{Name}] First EMG: {emg.RowCount}×{EmgChannelCount}");
            }

            Publish(emg);
            var lastRow = emg.Row(emg.RowCount - 1);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Viz?.Feed((Vector<double>)lastRow));
        }
    }

    private void HandleSingleEmg(JsonElement root)
    {
        var data = SiFiPacketParser.ParseChannelData(root);
        if (data is null) return;

        PacketsReceived++;

        // Try armband channels first (some devices send packet_type "emg" but have emg0–emg7)
        var matrix = SiFiPacketParser.ExtractEmgArmbandMatrix(data);
        if (matrix is not null && matrix.RowCount > 0)
        {
            SamplesPublished += matrix.RowCount;
            if (EmgChannelCount == 0) EmgChannelCount = matrix.ColumnCount;
            Publish(matrix as Matrix<double>);
            var lastRow = matrix.Row(matrix.RowCount - 1);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Viz?.Feed((Vector<double>)lastRow));
            return;
        }

        var emg = SiFiPacketParser.ExtractSingleEmg(data);
        if (emg is null) return;

        SamplesPublished += emg.Count;
        if (EmgChannelCount == 0) EmgChannelCount = 1;
        Publish(emg);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Viz?.Feed((Vector<double>)emg));
    }

    private void HandleImu(JsonElement root)
    {
        var data = SiFiPacketParser.ParseChannelData(root);
        if (data is null) return;

        var imu = SiFiPacketParser.ExtractImuVector(data);
        if (imu is null) return;

        Publish(("imu", imu));
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ScopeImu.EnqueueData(imu));
    }

    private void HandlePpg(JsonElement root)
    {
        var data = SiFiPacketParser.ParseChannelData(root);
        if (data is null) return;

        var ppg = SiFiPacketParser.ExtractPpgVector(data);
        if (ppg is null) return;

        Publish(("ppg", ppg));
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ScopePpg.EnqueueData(ppg));
    }

    private void HandleTaggedSensor(JsonElement root, string channelName, string tag, ScopeMonitor scope)
    {
        var data = SiFiPacketParser.ParseChannelData(root);
        if (data is null) return;

        var vec = SiFiPacketParser.ExtractSingleChannel(data, channelName);
        if (vec is null) return;

        // Single-channel sensors send multiple samples per packet — feed latest
        Publish((tag, vec));
        var latest = Vector<double>.Build.Dense((double[])new[] { vec[vec.Count - 1] });
        Avalonia.Threading.Dispatcher.UIThread.Post(() => scope.EnqueueData(latest));
    }

    private void HandleStatus(JsonElement root)
    {
        var data = SiFiPacketParser.ParseChannelData(root);
        if (data is null) return;

        if (SiFiPacketParser.TryParseBatteryLevel(data, out var level))
            BatteryLevel = level;

        if (SiFiPacketParser.TryParseTemperature(data, out var temp))
            Temperature = temp;
    }

    private void HandleDeviceInfoPacket(JsonElement root)
    {
        DeviceInfo = root.GetRawText();
        Debug.WriteLine($"[{Name}] Device info packet received");
    }

    private void HandleScanResults(JsonElement listElement)
    {
        var devices = SiFiPacketParser.ParseScanResults(listElement);
        Debug.WriteLine($"[{Name}] Scan found {devices.Count} devices");

        Action populate = () =>
        {
            foreach (var (name, address) in devices)
            {
                var display = $"{name} ({address})";
                if (!FoundDevices.Contains(display))
                {
                    FoundDevices.Add(display);
                    _displayToMac[display] = address;
                }
            }
        };

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            populate();
        else
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(populate);
    }

    private void HandleShow(JsonElement showElement)
    {
        DeviceInfo = showElement.GetRawText();
        Debug.WriteLine($"[{Name}] Show response received");
    }

    #endregion

    /// <summary>Source block — no upstream data processing.</summary>
    protected override void OnReceive(object sender, object data) { }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _bridge.Dispose();
        Viz?.Dispose();
        ScopeImu.Dispose();
        ScopeEcg.Dispose();
        ScopeEda.Dispose();
        ScopePpg.Dispose();
        base.Dispose();
    }

    private static string Flag(SensorFlags flags, SensorFlags sensor)
        => flags.HasFlag(sensor) ? "on" : "off";
}
