#if LINUX_BLE
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus;

namespace MOSAIC.Components.Manager.BLE;

/// <summary>
/// BLE backend for Linux using BlueZ over D-Bus.
/// </summary>
/// <remarks>
/// <para>
/// Requires the <c>Tmds.DBus</c> NuGet package and BlueZ ≥ 5.50 running on the host.
/// Compiled only when the <c>LINUX_BLE</c> preprocessor symbol is defined.
/// </para>
/// <para>
/// <b>csproj example:</b>
/// <code>
/// &lt;PropertyGroup Condition="$([MSBuild]::IsOSPlatform('Linux'))"&gt;
///   &lt;DefineConstants&gt;$(DefineConstants);LINUX_BLE&lt;/DefineConstants&gt;
/// &lt;/PropertyGroup&gt;
/// &lt;PackageReference Include="Tmds.DBus"
///                   Condition="$([MSBuild]::IsOSPlatform('Linux'))" /&gt;
/// </code>
/// </para>
/// <para>
/// <b>Architecture:</b> All BlueZ interaction goes through the system D-Bus bus.
/// Device discovery uses <c>org.bluez.Adapter1.StartDiscovery</c> then enumerates
/// managed objects via <c>org.freedesktop.DBus.ObjectManager.GetManagedObjects</c>.
/// GATT operations use <c>org.bluez.GattCharacteristic1</c>.
/// Notification delivery uses the <c>PropertiesChanged</c> signal on the characteristic
/// object path.
/// </para>
/// </remarks>
public sealed class LinuxBleBackend : IBleBackend
{
    private const string BluezService = "org.bluez";
    private const string AdapterPath = "/org/bluez/hci0";

    private Connection? _systemBus;

    /// <summary>
    /// Gets or lazily opens the system D-Bus connection.
    /// </summary>
    private async Task<Connection> GetBusAsync(CancellationToken ct)
    {
        if (_systemBus is not null)
            return _systemBus;

        _systemBus = new Connection(Address.System);
        await _systemBus.ConnectAsync().ConfigureAwait(false);
        return _systemBus;
    }

    /// <inheritdoc />
    public async Task<bool> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var bus = await GetBusAsync(cancellationToken).ConfigureAwait(false);
            var adapter = bus.CreateProxy<IAdapter1>(BluezService, new ObjectPath(AdapterPath));
            return await adapter.GetAsync<bool>("Powered").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LinuxBLE] Availability check failed: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BleDiscoveredDevice>> ScanAsync(int timeMs, CancellationToken cancellationToken = default)
    {
        var bus = await GetBusAsync(cancellationToken).ConfigureAwait(false);
        var adapter = bus.CreateProxy<IAdapter1>(BluezService, new ObjectPath(AdapterPath));

        try
        {
            await adapter.StartDiscoveryAsync().ConfigureAwait(false);
        }
        catch (DBusException ex) when (ex.ErrorName == "org.bluez.Error.InProgress")
        {
            // Already scanning — that's fine.
        }

        try
        {
            await Task.Delay(timeMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await adapter.StopDiscoveryAsync().ConfigureAwait(false); }
            catch { /* best-effort stop */ }
        }

        var objectManager = bus.CreateProxy<IObjectManager>(BluezService, new ObjectPath("/"));
        var objects = await objectManager.GetManagedObjectsAsync().ConfigureAwait(false);

        var devices = new List<BleDiscoveredDevice>();
        foreach (var (path, ifaces) in objects)
        {
            if (!ifaces.TryGetValue("org.bluez.Device1", out var props))
                continue;

            var address = props.TryGetValue("Address", out var addrObj) ? addrObj?.ToString() ?? "" : "";
            var name = props.TryGetValue("Name", out var nameObj) ? nameObj?.ToString() ?? "" : "";
            if (string.IsNullOrEmpty(name))
                name = props.TryGetValue("Alias", out var aliasObj) ? aliasObj?.ToString() ?? address : address;

            var peripheral = new LinuxBlePeripheral(bus, path, address, name);
            devices.Add(new BleDiscoveredDevice
            {
                Id = address,
                Name = name,
                Peripheral = peripheral
            });
        }

        Debug.WriteLine($"[LinuxBLE] Scan complete — {devices.Count} device(s) found.");
        return devices;
    }

    #region LinuxBlePeripheral

    private sealed class LinuxBlePeripheral : IBlePeripheral
    {
        private readonly Connection _bus;
        private readonly ObjectPath _devicePath;
        private readonly ConcurrentDictionary<(Guid, Guid), LinuxBleCharacteristic> _characteristics = new();

        public string Id { get; }
        public string Name { get; }

        public LinuxBlePeripheral(Connection bus, ObjectPath devicePath, string address, string name)
        {
            _bus = bus;
            _devicePath = devicePath;
            Id = address;
            Name = name;
        }

        public bool IsConnected
        {
            get
            {
                try
                {
                    var proxy = _bus.CreateProxy<IDevice1>(BluezService, _devicePath);
                    return proxy.GetAsync<bool>("Connected").GetAwaiter().GetResult();
                }
                catch { return false; }
            }
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            var proxy = _bus.CreateProxy<IDevice1>(BluezService, _devicePath);
            await proxy.ConnectAsync().ConfigureAwait(false);

            // Wait for ServicesResolved — BlueZ needs time to enumerate GATT after connect.
            await WaitForServicesResolvedAsync(proxy, cancellationToken).ConfigureAwait(false);
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            var proxy = _bus.CreateProxy<IDevice1>(BluezService, _devicePath);
            await proxy.DisconnectAsync().ConfigureAwait(false);
        }

        public async Task<IBleCharacteristic?> GetCharacteristicAsync(Guid serviceUuid, Guid characteristicUuid,
            CancellationToken cancellationToken = default)
        {
            var key = (serviceUuid, characteristicUuid);
            if (_characteristics.TryGetValue(key, out var cached))
                return cached;

            var objectManager = _bus.CreateProxy<IObjectManager>(BluezService, new ObjectPath("/"));
            var objects = await objectManager.GetManagedObjectsAsync().ConfigureAwait(false);

            var devicePrefix = _devicePath.ToString();
            var serviceUuidStr = serviceUuid.ToString().ToLowerInvariant();
            var charUuidStr = characteristicUuid.ToString().ToLowerInvariant();

            // First, find the service path.
            ObjectPath? servicePath = null;
            foreach (var (path, ifaces) in objects)
            {
                if (!path.ToString().StartsWith(devicePrefix, StringComparison.Ordinal))
                    continue;
                if (!ifaces.TryGetValue("org.bluez.GattService1", out var svcProps))
                    continue;
                if (svcProps.TryGetValue("UUID", out var uuid) &&
                    string.Equals(uuid?.ToString(), serviceUuidStr, StringComparison.OrdinalIgnoreCase))
                {
                    servicePath = path;
                    break;
                }
            }

            if (servicePath is null)
            {
                Debug.WriteLine($"[LinuxBLE] Service {serviceUuidStr} not found on {Name}.");
                return null;
            }

            // Then, find the characteristic path under that service.
            var svcPrefix = servicePath.Value.ToString();
            foreach (var (path, ifaces) in objects)
            {
                if (!path.ToString().StartsWith(svcPrefix, StringComparison.Ordinal))
                    continue;
                if (!ifaces.TryGetValue("org.bluez.GattCharacteristic1", out var charProps))
                    continue;
                if (charProps.TryGetValue("UUID", out var uuid) &&
                    string.Equals(uuid?.ToString(), charUuidStr, StringComparison.OrdinalIgnoreCase))
                {
                    var characteristic = new LinuxBleCharacteristic(_bus, path, characteristicUuid);
                    return _characteristics.GetOrAdd(key, _ => characteristic);
                }
            }

            Debug.WriteLine($"[LinuxBLE] Characteristic {charUuidStr} not found under {svcPrefix}.");
            return null;
        }

        /// <summary>
        /// Waits for BlueZ to finish GATT service resolution after a connection.
        /// Without this, <c>GetCharacteristicAsync</c> would find nothing.
        /// </summary>
        private static async Task WaitForServicesResolvedAsync(IDevice1 device, CancellationToken ct)
        {
            const int maxWaitMs = 10_000;
            const int pollMs = 100;
            int elapsed = 0;

            while (elapsed < maxWaitMs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (await device.GetAsync<bool>("ServicesResolved").ConfigureAwait(false))
                        return;
                }
                catch { /* property may not exist yet */ }

                await Task.Delay(pollMs, ct).ConfigureAwait(false);
                elapsed += pollMs;
            }

            Debug.WriteLine("[LinuxBLE] Timed out waiting for ServicesResolved.");
        }
    }

    #endregion

    #region LinuxBleCharacteristic

    private sealed class LinuxBleCharacteristic : IBleCharacteristic
    {
        private readonly Connection _bus;
        private readonly ObjectPath _charPath;
        private IDisposable? _notifyWatcher;

        public Guid Uuid { get; }
        public event EventHandler<byte[]>? ValueChanged;

        public LinuxBleCharacteristic(Connection bus, ObjectPath charPath, Guid uuid)
        {
            _bus = bus;
            _charPath = charPath;
            Uuid = uuid;
        }

        public async Task WriteValueWithoutResponseAsync(byte[] value, CancellationToken cancellationToken = default)
        {
            var proxy = _bus.CreateProxy<IGattCharacteristic1>(BluezService, _charPath);
            var options = new Dictionary<string, object> { ["type"] = "command" };
            await proxy.WriteValueAsync(value, options).ConfigureAwait(false);
        }

        public async Task StartNotificationsAsync(CancellationToken cancellationToken = default)
        {
            var proxy = _bus.CreateProxy<IGattCharacteristic1>(BluezService, _charPath);

            // Subscribe to PropertiesChanged on this object path.
            var propsProxy = _bus.CreateProxy<IProperties>(BluezService, _charPath);
            _notifyWatcher = await propsProxy.WatchPropertiesAsync(change =>
            {
                foreach (var kvp in change.Changed)
                {
                    if (kvp.Key == "Value" && kvp.Value is byte[] data)
                        ValueChanged?.Invoke(this, data);
                }
            }).ConfigureAwait(false);

            await proxy.StartNotifyAsync().ConfigureAwait(false);
        }
    }

    #endregion

    #region BlueZ D-Bus interface definitions

    //  Minimal D-Bus proxy interfaces for BlueZ.
    //  These are used by Tmds.DBus to generate method calls
    //  and signal subscriptions at runtime.

    /// <summary>org.freedesktop.DBus.ObjectManager — enumerates all objects under a path.</summary>
    [DBusInterface("org.freedesktop.DBus.ObjectManager")]
    private interface IObjectManager : IDBusObject
    {
        /// <summary>
        /// Returns every managed object path with its interfaces and properties.
        /// </summary>
        Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();
    }

    /// <summary>org.freedesktop.DBus.Properties — generic property access and change signals.</summary>
    [DBusInterface("org.freedesktop.DBus.Properties")]
    private interface IProperties : IDBusObject
    {
        Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
    }

    /// <summary>org.bluez.Adapter1 — BLE adapter (typically <c>/org/bluez/hci0</c>).</summary>
    [DBusInterface("org.bluez.Adapter1")]
    private interface IAdapter1 : IDBusObject
    {
        Task StartDiscoveryAsync();
        Task StopDiscoveryAsync();
        Task<T> GetAsync<T>(string prop);
    }

    /// <summary>org.bluez.Device1 — a discovered/connected BLE device.</summary>
    [DBusInterface("org.bluez.Device1")]
    private interface IDevice1 : IDBusObject
    {
        Task ConnectAsync();
        Task DisconnectAsync();
        Task<T> GetAsync<T>(string prop);
    }

    /// <summary>org.bluez.GattCharacteristic1 — a single GATT characteristic.</summary>
    [DBusInterface("org.bluez.GattCharacteristic1")]
    private interface IGattCharacteristic1 : IDBusObject
    {
        Task<byte[]> ReadValueAsync(IDictionary<string, object> options);
        Task WriteValueAsync(byte[] value, IDictionary<string, object> options);
        Task StartNotifyAsync();
        Task StopNotifyAsync();
        Task<T> GetAsync<T>(string prop);
    }

    #endregion
}
#endif
