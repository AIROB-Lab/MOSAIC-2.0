using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MOSAIC.Diagnostics;

namespace MOSAIC.Components.Manager.BLE;

/// <summary>
/// Singleton manager for Bluetooth Low Energy device discovery, connection, and command dispatch.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BleManager"/> is consumed by any <c>BaseBlock</c> that requires BLE communication
/// (e.g. <c>Myo</c>, future BLE sensor blocks). Because it is a singleton, a single scan/connect
/// surface is shared across all blocks in the pipeline.
/// </para>
/// <para>
/// <b>Usage pattern (from a block):</b>
/// <code>
/// var mgr = BleManager.Instance;
/// await mgr.ScanForBleDevicesAsync(5000, ct);
/// var device = await mgr.ConnectToDeviceAsync("MyoArmband", sampleLength: 16, ct);
/// await mgr.SendCommandAsync(device, vibrationCmd, ct);
/// await mgr.DisconnectDeviceAsync(device, ct);
/// </code>
/// </para>
/// <para>
/// <b>Thread safety:</b> All public methods are safe to call from any thread.
/// <see cref="DiscoveredDevices"/> is replaced atomically after each scan; consumers should
/// snapshot the reference before iterating.
/// </para>
/// </remarks>
public sealed class BleManager : IDisposable
{
    #region Singleton

    private static readonly Lazy<BleManager> _instance = new(() => new BleManager());
    private static Func<IBleBackend>? _platformBackendFactory;

    /// <summary>Gets the process-wide singleton instance.</summary>
    public static BleManager Instance => _instance.Value;

    /// <summary>
    /// Registers the BLE implementation supplied by a platform head before the singleton is used.
    /// </summary>
    /// <remarks>
    /// The shared MOSAIC assembly targets plain <c>net10.0</c>, so Android/CoreBluetooth types
    /// cannot be compiled into it. Mobile heads register their native backend during bootstrap.
    /// </remarks>
    public static void ConfigurePlatformBackend(Func<IBleBackend> backendFactory)
    {
        ArgumentNullException.ThrowIfNull(backendFactory);

        if (_instance.IsValueCreated)
        {
            throw new InvalidOperationException(
                "The BLE platform backend must be configured before BleManager.Instance is first used.");
        }

        _platformBackendFactory = backendFactory;
    }

    #endregion

    #region Fields

    private readonly IBleBackend _backend;
    private readonly object _lock = new();
    private bool _isScanning;
    private bool _disposed;

    #endregion

    #region Properties

    /// <summary>
    /// Devices discovered during the most recent <see cref="ScanForBleDevicesAsync"/> call.
    /// The list is replaced atomically on each scan; take a local reference before iterating.
    /// </summary>
    public IReadOnlyList<BleDiscoveredDevice> DiscoveredDevices { get; private set; } = [];

    /// <summary>
    /// Currently connected devices keyed by their display name.
    /// </summary>
    public Dictionary<string, BleDevice> ConnectedDevices { get; } = [];

    /// <summary>
    /// <see langword="true"/> while a BLE scan is in progress.
    /// </summary>
    public bool IsScanning
    {
        get { lock (_lock) return _isScanning; }
        private set { lock (_lock) _isScanning = value; }
    }

    #endregion

    #region Events

    /// <summary>
    /// Raised after a device is successfully connected and added to <see cref="ConnectedDevices"/>.
    /// The argument is the newly connected <see cref="BleDevice"/>.
    /// </summary>
    public event EventHandler<BleDevice>? DeviceConnected;

    /// <summary>
    /// Raised after a device is disconnected and removed from <see cref="ConnectedDevices"/>.
    /// The argument is the disconnected <see cref="BleDevice"/>.
    /// </summary>
    public event EventHandler<BleDevice>? DeviceDisconnected;

    /// <summary>
    /// Raised when a BLE scan completes. The argument is the number of devices found.
    /// </summary>
    public event EventHandler<int>? ScanCompleted;

    #endregion

    #region Constructor

    /// <summary>
    /// Private constructor — use <see cref="Instance"/> to obtain the singleton.
    /// </summary>
    private BleManager() : this(CreateBackend())
    {
    }

    internal BleManager(IBleBackend backend)
    {
        _backend = backend;
    }

    #endregion

    #region Scanning

    /// <summary>
    /// Scans for BLE devices for the specified duration.
    /// </summary>
    /// <param name="timeMs">Scan duration in milliseconds (default 5 000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="PlatformNotSupportedException">
    /// Bluetooth is not supported on this platform.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The platform BLE backend failed to initialise or scan.
    /// </exception>
    public async Task ScanForBleDevicesAsync(int timeMs = 5000, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IsScanning = true;
        try
        {
            bool available = await _backend.GetAvailabilityAsync(ct).ConfigureAwait(false);
            if (!available)
            {
                Log.Warn("BleManager", "Bluetooth is not available.");
                DiscoveredDevices = [];
                return;
            }

            Debug.WriteLine($"[BleManager] Starting BLE scan ({timeMs} ms)...");
            var results = await _backend.ScanAsync(timeMs, ct).ConfigureAwait(false);
            DiscoveredDevices = results;
            Debug.WriteLine($"[BleManager] Scan complete — {results.Count} device(s) found.");

            ScanCompleted?.Invoke(this, results.Count);
        }
        catch (PlatformNotSupportedException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "BLE scan failed while initialising the platform Bluetooth backend.", ex);
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Synchronous-signature wrapper kept for backward compatibility with existing blocks.
    /// Prefer <see cref="ScanForBleDevicesAsync"/> in new code.
    /// </summary>
    public Task ScanForBleDevices(int timeMs = 5000)
        => ScanForBleDevicesAsync(timeMs);

    #endregion

    #region Connection

    /// <summary>
    /// Connects to a previously discovered BLE device by its peripheral handle.
    /// </summary>
    /// <param name="device">The discovered device (from <see cref="DiscoveredDevices"/>).</param>
    /// <param name="sampleLength">
    /// Expected byte count per EMG sample, passed through to <see cref="BleDevice"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The connected <see cref="BleDevice"/>, or <see langword="null"/> on failure.</returns>
    public async Task<BleDevice?> ConnectToBluetoothDeviceAsync(
        BleDiscoveredDevice device, int sampleLength, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            if (!device.Peripheral.IsConnected)
                await device.Peripheral.ConnectAsync(ct).ConfigureAwait(false);

            if (!device.Peripheral.IsConnected)
                return null;

            var bleDevice = new BleDevice(device.Name, device.Peripheral, sampleLength);
            ConnectedDevices[device.Name] = bleDevice;

            Debug.WriteLine($"[BleManager] Connected to device: {device.Name}");
            DeviceConnected?.Invoke(this, bleDevice);
            return bleDevice;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("BleManager", ex, "Connection error.");
            return null;
        }
    }

    /// <summary>
    /// Backward-compatible overload without <see cref="CancellationToken"/>.
    /// </summary>
    public Task<BleDevice?> ConnectToBluetoothDevice(BleDiscoveredDevice device, int sampleLength)
        => ConnectToBluetoothDeviceAsync(device, sampleLength);

    /// <summary>
    /// Finds a discovered device whose name contains <paramref name="name"/> and connects to it.
    /// A scan must have been completed first via <see cref="ScanForBleDevicesAsync"/>.
    /// </summary>
    /// <param name="name">Substring to match against discovered device names (case-sensitive).</param>
    /// <param name="sampleLength">Expected byte count per EMG sample.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The connected <see cref="BleDevice"/>, or <see langword="null"/> if not found.</returns>
    public async Task<BleDevice?> ConnectToDeviceAsync(
        string name, int sampleLength, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var devices = DiscoveredDevices;
        if (devices.Count == 0)
        {
            Log.Warn("BleManager", $"ConnectToDevice(\"{name}\") failed — no scan results. Call ScanForBleDevicesAsync() first.");
            return null;
        }

        // Small yield to let any pending BLE operations settle.
        await Task.Delay(50, ct).ConfigureAwait(false);

        foreach (var device in devices)
        {
            if (device.Name.Contains(name, StringComparison.Ordinal))
                return await ConnectToBluetoothDeviceAsync(device, sampleLength, ct).ConfigureAwait(false);
        }

        Debug.WriteLine($"[BleManager] No device matching \"{name}\" found in {devices.Count} scan results.");
        return null;
    }

    /// <summary>
    /// Backward-compatible overload that matches the old <c>ConnectToDevice</c> signature.
    /// </summary>
    public Task<BleDevice?> ConnectToDevice(string name, int sampleLength)
        => ConnectToDeviceAsync(name, sampleLength);

    #endregion

    #region Disconnection

    /// <summary>
    /// Disconnects a single device and removes it from <see cref="ConnectedDevices"/>.
    /// </summary>
    public async Task DisconnectDeviceAsync(BleDevice device, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A lost link can still own native GATT resources that need closing.
        await device.Peripheral.DisconnectAsync(ct).ConfigureAwait(false);

        ConnectedDevices.Remove(device.Name);
        Debug.WriteLine($"[BleManager] Disconnected device: {device.Name}");
        DeviceDisconnected?.Invoke(this, device);
    }

    /// <summary>
    /// Backward-compatible overload without <see cref="CancellationToken"/>.
    /// </summary>
    public Task DisconnectDevice(BleDevice device)
        => DisconnectDeviceAsync(device);

    /// <summary>
    /// Disconnects every connected device and clears <see cref="ConnectedDevices"/>.
    /// </summary>
    public async Task DisconnectAllDevicesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var device in ConnectedDevices.Values.ToList())
        {
            try
            {
                if (device.Peripheral.IsConnected)
                    await device.Peripheral.DisconnectAsync(ct).ConfigureAwait(false);

                DeviceDisconnected?.Invoke(this, device);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BleManager] Error disconnecting {device.Name}: {ex.Message}");
            }
        }

        ConnectedDevices.Clear();
    }

    /// <summary>
    /// Backward-compatible overload matching the old <c>DisconnectAllDevices</c> signature.
    /// </summary>
    public Task DisconnectAllDevices()
        => DisconnectAllDevicesAsync();

    #endregion

    #region Commands & Notifications

    /// <summary>
    /// Writes a BLE command using the backend's supported control-write mode.
    /// </summary>
    /// <param name="device">The connected device.</param>
    /// <param name="command">The command to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// The target characteristic was not found on the device.
    /// </exception>
    public async Task SendCommandAsync(BleDevice device, BleCommand command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var characteristic = await device.Peripheral
            .GetCharacteristicAsync(Guid.Parse(command.Service), Guid.Parse(command.Characteristic), ct)
            .ConfigureAwait(false);

        if (characteristic is null)
            throw new InvalidOperationException(
                $"Characteristic {command.Characteristic} not found on service {command.Service}.");

        await characteristic.WriteValueAsync(command.Cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Backward-compatible overload matching the old <c>SendCommand</c> signature.
    /// </summary>
    public Task SendCommand(BleDevice device, BleCommand command)
        => SendCommandAsync(device, command);

    /// <summary>
    /// Subscribes to BLE notifications on one characteristic and wires incoming data
    /// to <see cref="BleDevice.HandleCharacteristicValue"/>.
    /// </summary>
    /// <param name="device">The connected device.</param>
    /// <param name="command">
    /// Identifies the service/characteristic to subscribe to.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StartNotificationAsync(BleDevice device, BleCommand command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var characteristic = await device.Peripheral
            .GetCharacteristicAsync(Guid.Parse(command.Service), Guid.Parse(command.Characteristic), ct)
            .ConfigureAwait(false);

        if (characteristic is null)
            throw new InvalidOperationException(
                $"Notification characteristic {command.Characteristic} not found on {device.Name}.");

        Console.WriteLine($"[BleManager] StartNotification: subscribing to {characteristic.Uuid} on {device.Name}");
        EventHandler<byte[]> handler = (_, data) => device.HandleCharacteristicValue(data);
        characteristic.ValueChanged += handler;
        try
        {
            await characteristic.StartNotificationsAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            characteristic.ValueChanged -= handler;
            throw;
        }
        Console.WriteLine($"[BleManager] Notification setup complete for {device.Name}");
    }

    /// <summary>
    /// Runs a list of notification-subscription commands sequentially on the given device.
    /// </summary>
    /// <param name="device">The connected device.</param>
    /// <param name="commands">Notification commands to set up.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ConnectionRoutineAsync(BleDevice device, List<BleCommand> commands, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var command in commands)
        {
            ct.ThrowIfCancellationRequested();
            await StartNotificationAsync(device, command, ct).ConfigureAwait(false);
        }

        Debug.WriteLine($"[BleManager] Connection routine completed for {device.Name} ({commands.Count} notifications).");
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Disconnects all devices and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            DisconnectAllDevicesAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BleManager] Dispose cleanup error: {ex.Message}");
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Creates the appropriate platform-specific BLE backend.
    /// </summary>
    /// <remarks>
    /// <para>Selection order (all native — no InTheHand dependency):</para>
    /// <list type="number">
    ///   <item><b>Windows</b> — <c>WindowsBleBackend</c> (WinRT <c>Windows.Devices.Bluetooth</c>).
    ///     Requires <c>net10.0-windows10.0.19041.0</c> TFM (auto-defines <c>WINDOWS10_0_19041_0_OR_GREATER</c>).</item>
    ///   <item><b>Android</b> — <c>AndroidBleBackend</c> (<c>Android.Bluetooth</c>).
    ///     Requires Android TFM (auto-defines <c>ANDROID</c>).</item>
    ///   <item><b>macOS / iOS</b> — <c>AppleBleBackend</c> (CoreBluetooth).
    ///     Requires <c>APPLE_BLE</c> define.</item>
    ///   <item><b>Linux</b> — <c>LinuxBleBackend</c> (BlueZ D-Bus).
    ///     Requires <c>LINUX_BLE</c> define and <c>Tmds.DBus</c> NuGet.</item>
    /// </list>
    /// </remarks>
    private static IBleBackend CreateBackend()
    {
        if (_platformBackendFactory is { } platformFactory)
        {
            Debug.WriteLine("[BleManager] Using backend registered by the platform head.");
            return platformFactory();
        }

#if WINDOWS10_0_19041_0_OR_GREATER
        Debug.WriteLine("[BleManager] Using native Windows BLE backend (WinRT).");
        return new WindowsBleBackend();
#endif

#if ANDROID
        Debug.WriteLine("[BleManager] Using native Android BLE backend.");
        return new AndroidBleBackend();
#endif

#if APPLE_BLE
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Create("iOS")) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Create("MacCatalyst")))
        {
            Debug.WriteLine("[BleManager] Using native Apple BLE backend (CoreBluetooth).");
            return new AppleBleBackend();
        }
#endif

#if LINUX_BLE
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Debug.WriteLine("[BleManager] Using native Linux BLE backend (BlueZ D-Bus).");
            return new LinuxBleBackend();
        }
#endif

        throw new PlatformNotSupportedException(
            "No BLE backend available for this platform. " +
            "Windows requires net8.0-windows10.0.19041.0 TFM; " +
            "Android requires an Android TFM; " +
            "macOS/iOS requires APPLE_BLE define; " +
            "Linux requires LINUX_BLE define + Tmds.DBus.");
    }

    #endregion
}
