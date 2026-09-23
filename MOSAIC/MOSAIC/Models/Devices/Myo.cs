using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Manager.BLE;
using MOSAIC.Components.Manager.BLE.Commands;
using MOSAIC.Diagnostics;
using MOSAIC.Visualization;

namespace MOSAIC.Models.Devices;

/// <summary>
/// High-performance Myo armband EMG source block (8 channels, BLE).
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> Streams 8-channel surface EMG from a Thalmic Myo armband over Bluetooth Low Energy.
/// Each tick dequeues one BLE sample, decodes it into a <see cref="Vector{T}"/> of <see cref="double"/>
/// in the range [−1.0, +0.992], and publishes it downstream. The block is tick-driven — an upstream
/// clock block provides the cadence; the Myo's own sample rate is not used directly.
/// </para>
/// <para>
/// <b>Sample ownership:</b> Acquisition reuses a private buffer. Each publication copies it,
/// so queued consumers and recordings retain the sample even after later BLE packets arrive.
/// </para>
/// <para>
/// <b>Lookup table decode:</b> A 256-entry LUT (<c>_byteToDouble</c>) maps each raw <see cref="byte"/>
/// [0, 255] to a <see cref="double"/> in [−1.0, +0.992] via signed reinterpretation
/// (<c>(sbyte)b / 128.0</c>), eliminating per-sample division on the hot path.
/// </para>
/// <para>
/// <b>BLE buffer management:</b> <see cref="BleDevice.ShortenBuffer"/> is called each tick to keep
/// BLE latency bounded to at most <see cref="MaxDelay"/> packets, discarding older samples when the
/// consumer falls behind.
/// </para>
/// <para>
/// <b>BLE lifecycle:</b>
/// <list type="number">
///   <item><description>
///     <see cref="ScanAsync"/> — scans for 5 seconds, populates <see cref="FoundDevices"/>
///     (filtered by optional <see cref="MyoName"/> substring).
///   </description></item>
///   <item><description>
///     <see cref="ConnectAsync"/> — connects, sends vibration feedback, runs the BLE connection
///     routine, and configures the Myo for filtered EMG streaming (no IMU, no classifier).
///   </description></item>
///   <item><description>
///     <see cref="DisconnectAsync"/> — sends short vibration feedback, disconnects, and zeroes
///     the write buffer for clean UI decay.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified in the JSON configuration, the base class
/// <see cref="BaseBlock.Dumper"/> automatically logs every published vector as
/// <c>timestamp, ch₀, ch₁, …, ch₇</c> via <see cref="BaseBlock.Publish"/>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "MyMyo": {
///     "Type": "Myo",
///     "DesiredRate": 200,
///     "Inputs": [ "Clock" ],
///     "Params": [ "MyArmband" ],
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader>
///     <term>Index</term>
///     <description>Description</description>
///   </listheader>
///   <item>
///     <term>0</term>
///     <description>
///       <c>MyoName</c> (string, optional). Substring filter applied during BLE scan.
///       Only devices whose name contains this value (case-insensitive) are shown
///       in <see cref="FoundDevices"/>. If omitted, all discovered devices are listed.
///     </description>
///   </item>
/// </list>
/// </para>
/// </example>
public sealed class Myo : BaseBlock
{
    
        #region Fields
    /// <summary>
    /// Singleton BLE manager shared across all BLE device blocks.
    /// </summary>
    private readonly BleManager _bleManager;

    /// <summary>
    /// The connected BLE device handle, or <see langword="null"/> if not connected.
    /// </summary>
    private BleDevice? _dev;

    /// <summary>
    /// Myo-specific BLE command definitions (vibration, EMG mode, sleep mode, etc.).
    /// </summary>
    private readonly MyoCommandDefinitions _cmd = new();
    

    /// <summary>
    /// Number of EMG channels on the Myo armband.
    /// </summary>
    private const int Channels = 8;

    /// <summary>
    /// Precomputed lookup table mapping each byte [0, 255] to a double in [−1.0, +0.992].
    /// Eliminates per-sample signed reinterpretation and division on the hot path.
    /// </summary>
    private static readonly double[] _byteToDouble = BuildLut();

    // Reused only for acquisition; every publication gets its own snapshot.
    private readonly DenseVector _writeVec = DenseVector.Create(Channels, 0.0);
    private readonly double[] _writeArr;

    /// <summary>
    /// Maximum number of BLE packets allowed to accumulate before older ones are discarded.
    /// Keeps streaming latency bounded.
    /// </summary>
    private const int MaxDelay = 15;

    /// <summary>
    /// Scale factor for Myo EMG data. Raw signed bytes are divided by this value
    /// to produce the [−1.0, +0.992] range. Used to build <see cref="_byteToDouble"/>.
    /// </summary>
    private const double MYOHW_EMG_SCALE = 128.0;

    #endregion

    #region Properties
    
    /// <summary>
    /// Visualization helper for binding the EMG signal to the UI scope.
    /// </summary>
    public BlockVisualization? Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>
    /// Optional substring filter applied during <see cref="ScanAsync"/> to restrict
    /// <see cref="FoundDevices"/> to matching Myo armbands.
    /// </summary>
    public string? MyoName { get; set; }

    /// <summary>
    /// Gets a value indicating whether the Myo is currently connected and streaming.
    /// </summary>
    private volatile bool _streamReady;
    public bool IsConnected => _streamReady && _dev?.Peripheral.IsConnected == true;

    /// <summary>The last connection/setup failure, displayed on the Myo card.</summary>
    public string? LastConnectionError { get; private set; }

    /// <summary>
    /// Observable collection of discovered BLE device names, populated by <see cref="ScanAsync"/>.
    /// Bound to the UI device picker.
    /// </summary>
    public ObservableCollection<string> FoundDevices { get; } = new();
    #endregion
    
    /// <summary>
    /// Initializes a new <see cref="Myo"/> block with a private acquisition buffer ready for streaming.
    /// </summary>
    /// <param name="name">Display name of the block.</param>
    /// <param name="desiredRate">
    /// Desired processing rate in Hz. Defaults to 200 (Myo's native EMG sample rate).
    /// </param>
    public Myo(string name = "Myo", double desiredRate = 200) : this(BleManager.Instance, name, desiredRate)
    {
    }

    internal Myo(BleManager bleManager, string name = "Myo", double desiredRate = 200)
    {
        _bleManager = bleManager;
        Name = name;
        DesiredRate = desiredRate;

        _writeArr = _writeVec.AsArray();
    }
    

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_emg.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_emg";

    /// <summary>
    /// Creates and configures a <see cref="Myo"/> block from a JSON model definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// The JSON model. See the class-level example for the expected <c>Params</c> layout.
    /// </param>
    /// <returns>A fully configured <see cref="Myo"/> instance.</returns>
    /// <seealso cref="Myo"/>
    public static Myo ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "Myo";
        var rate = m.DesiredRate ?? 200;
        string? myoName = m.Params is { Count: > 0 } ? JsonModel.GetString(m.Params[0]) : null;

        var block = ActivatorUtilities.CreateInstance<Myo>(sp, name, rate);
        block.MyoName = myoName;
        
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "Myo";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
        => MyoName is not null ? new object[] { MyoName } : null;

    #endregion
    

    /// <summary>
    /// Releases all resources: visualization, BLE connection, and base class resources
    /// (including the CSV dumper if configured).
    /// </summary>
    public override void Dispose()
    {
        try { Viz?.Dispose(); } catch { /* no-op */ }

        base.Dispose();

        try
        {
            if (_dev is not null) TrackCleanup(_bleManager.DisconnectDevice(_dev));
        }
        catch { /* no-op */ }
    }
    

    /// <summary>
    /// Processes one tick: dequeues a BLE sample (if available), decodes it into the write buffer,
    /// and publishes the current vector.
    /// </summary>
    /// <param name="sender">The upstream clock block.</param>
    /// <param name="tNow">Tick payload (ignored — timing comes from the clock block's cadence).</param>
    /// <remarks>
    /// <para>
    /// <b>BLE decode:</b> Raw bytes are converted to doubles via the precomputed
    /// <see cref="_byteToDouble"/> LUT — no division, no branching per sample.
    /// If the BLE packet is shorter than <see cref="Channels"/>, trailing channels
    /// retain their previous values (graceful degradation).
    /// </para>
    /// <para>
    /// <b>Buffer management:</b> <see cref="BleDevice.ShortenBuffer"/> is called each tick
    /// to cap BLE queue depth at <see cref="MaxDelay"/> packets.
    /// </para>
    /// </remarks>
    protected override void OnReceive(object sender, object tNow)
    {
        var d = _dev;
        if (IsConnected && d is not null)
        {
            try { _ = d.ShortenBuffer(MaxDelay); } catch { /* stay realtime */ }

            var raw = d.DequeueBuffer();
            if (raw is not null)
            {
                int n = raw.Length < Channels ? raw.Length : Channels;
                for (int i = 0; i < n; i++)
                    _writeArr[i] = _byteToDouble[raw[i]];
            }
        }

        PublishCurrent();
    }

    /// <summary>Publishes a stable copy of the latest acquired sample.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PublishCurrent()
    {
        var output = _writeVec.Clone();
        Publish(output);
        Viz?.Feed(output);
    }

    /// <summary>
    /// Scans for BLE devices for 5 seconds and populates <see cref="FoundDevices"/>.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <remarks>
    /// <para>
    /// If <see cref="MyoName"/> is set, only devices whose name contains the substring
    /// (case-insensitive) are added to <see cref="FoundDevices"/>.
    /// </para>
    /// <para>
    /// All collection mutations are marshalled to the UI thread since
    /// <see cref="FoundDevices"/> is an <see cref="ObservableCollection{T}"/>.
    /// </para>
    /// </remarks>
    public async ValueTask ScanAsync(CancellationToken ct = default)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            FoundDevices.Clear();
        else
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => FoundDevices.Clear());

        await _bleManager.ScanForBleDevicesAsync(5000, ct);

        var devices = _bleManager.DiscoveredDevices.ToList();
        Action populateAction = () =>
        {
            foreach (var device in devices)
            {
                string displayName = string.IsNullOrEmpty(device.Name) ? device.Id : device.Name;

                if (MyoName == null || displayName.Contains(MyoName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!FoundDevices.Contains(displayName))
                        FoundDevices.Add(displayName);
                }
            }

            Debug.WriteLine($"[Myo] Scan complete — {FoundDevices.Count} device(s) found");
        };

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            populateAction();
        else
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(populateAction);
    }

    /// <summary>
    /// Connects to the specified Myo device and configures it for filtered EMG streaming.
    /// </summary>
    /// <param name="selectedDeviceName">
    /// Display name of the device to connect to (must match an entry in <see cref="FoundDevices"/>).
    /// </param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> if the connection succeeded and EMG streaming was configured;
    /// <see langword="false"/> otherwise.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Connection sequence:</b>
    /// <list type="number">
    ///   <item><description>Vibrate long ×2 (tactile feedback that pairing started).</description></item>
    ///   <item><description>Run the BLE connection routine (service/characteristic discovery).</description></item>
    ///   <item><description>Set EMG mode: filtered EMG, no IMU, no classifier.</description></item>
    ///   <item><description>Disable auto-sleep to keep the armband streaming.</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public async ValueTask<bool> ConnectAsync(string selectedDeviceName, CancellationToken ct = default)
    {
        LastConnectionError = null;
        if (IsConnected) return true;
        if (_dev is not null) await DisconnectAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selectedDeviceName))
        {
            LastConnectionError = "Select a Myo device first.";
            return false;
        }
        Console.WriteLine($"[Myo] Looking for '{selectedDeviceName}' in {_bleManager.DiscoveredDevices.Count} devices:");
        foreach (var d in _bleManager.DiscoveredDevices)
            Console.WriteLine($"[Myo]   Name='{d.Name}' Id='{d.Id}'");
        try
        {
            var device = _bleManager.DiscoveredDevices.FirstOrDefault(d =>
            {
                string name = string.IsNullOrEmpty(d.Name) ? d.Id : d.Name;
                return name.Equals(selectedDeviceName, StringComparison.OrdinalIgnoreCase)
                       || d.Id.Equals(selectedDeviceName, StringComparison.OrdinalIgnoreCase)
                       || d.Name.Contains(selectedDeviceName, StringComparison.OrdinalIgnoreCase)
                       || selectedDeviceName.Contains(d.Name, StringComparison.OrdinalIgnoreCase);
            });
            

            if (device == null)
            {
                LastConnectionError = $"Device '{selectedDeviceName}' was not found. Scan again.";
                return false;
            }

            var connectedDevice = await _bleManager.ConnectToBluetoothDeviceAsync(device, Channels, ct).ConfigureAwait(false);

            if (connectedDevice != null)
            {
                _dev = connectedDevice;
                await _bleManager.SendCommandAsync(_dev, _cmd.GetCommand(MyoCommandEnum.VibrateLong2), ct).ConfigureAwait(false);
                await _bleManager.SendCommandAsync(_dev, _cmd.GetCommand(MyoCommandEnum.VibrateLong), ct).ConfigureAwait(false);
                await _bleManager.ConnectionRoutineAsync(_dev, _cmd.GetInitialNotificationCommands(), ct).ConfigureAwait(false);
                await _bleManager.SendCommandAsync(_dev, _cmd.GetCommand(MyoCommandEnum.FiltEMG_NoIMU_NoClass), ct).ConfigureAwait(false);
                await _bleManager.SendCommandAsync(_dev, _cmd.GetCommand(MyoCommandEnum.NoSleep), ct).ConfigureAwait(false);
                try
                {
                    await _dev.WaitForFirstSampleAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException("Bluetooth connected, but the Myo sent no EMG data. Wake the armband and reconnect.");
                }
                if (!_dev.Peripheral.IsConnected)
                    throw new InvalidOperationException("Myo disconnected during EMG setup.");
                _streamReady = true;
                Log.Info("Myo", $"EMG data received from {selectedDeviceName}; streaming ready. Start the connected Clock to plot it.");
                Console.WriteLine($"[Myo] EMG streaming confirmed from {selectedDeviceName}.");
                return true;
            }

            LastConnectionError = "Could not connect to the Myo. Check that it is awake and not connected to another app.";
            return false;
        }
        catch (Exception ex)
        {
            LastConnectionError = ex.Message;
            Log.Error("Myo", ex, "EMG connection/setup failed.");
            Console.WriteLine($"[Myo] EMG connection/setup failed: {ex.Message}");
            _streamReady = false;
            var failedDevice = _dev;
            _dev = null;
            if (failedDevice is not null)
            {
                try { await _bleManager.DisconnectDeviceAsync(failedDevice).ConfigureAwait(false); }
                catch (Exception cleanupError) { Log.Error("Myo", cleanupError, "Failed to close Bluetooth connection."); }
            }
            if (ex is OperationCanceledException) throw;
            return false;
        }
    }

    /// <summary>
    /// Disconnects from the Myo armband and zeroes the write buffer for clean UI decay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Disconnect sequence:</b>
    /// <list type="number">
    ///   <item><description>Vibrate short ×2 (tactile feedback that disconnect started).</description></item>
    ///   <item><description>Disconnect the BLE device.</description></item>
    ///   <item><description>Zero the current write buffer so the scope decays to zero smoothly.</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public async ValueTask DisconnectAsync()
    {
        var dev = _dev;
        _streamReady = false;
        _dev = null;
        if (dev is null) return;

        try
        {
            await _bleManager.SendCommand(dev, _cmd.GetCommand(MyoCommandEnum.VibrateShort2)).ConfigureAwait(false);
            await _bleManager.SendCommand(dev, _cmd.GetCommand(MyoCommandEnum.VibrateShort)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Myo] Disconnect error: {ex.Message}");
        }
        finally
        {
            try { await _bleManager.DisconnectDeviceAsync(dev).ConfigureAwait(false); }
            finally
            {
                Array.Clear(_writeArr);
            }
        }
    }
    
    /// <summary>
    /// Builds the 256-entry byte-to-double lookup table.
    /// Each byte is reinterpreted as a signed value (<see cref="sbyte"/>) and divided by
    /// <see cref="MYOHW_EMG_SCALE"/> (128.0), producing a range of [−1.0, +0.992].
    /// </summary>
    /// <returns>A 256-element <see cref="double"/> array indexed by raw byte value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double[] BuildLut()
    {
        var lut = new double[256];
        for (int b = 0; b < 256; b++)
            lut[b] = unchecked((sbyte)b) / MYOHW_EMG_SCALE;
        return lut;
    }
}
