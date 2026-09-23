#if ANDROID
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Debug = System.Diagnostics.Debug;
using MOSAIC.Diagnostics;

namespace MOSAIC.Components.Manager.BLE;

/// <summary>
/// Native Android BLE backend using <c>Android.Bluetooth</c> APIs.
/// </summary>
/// <remarks>
/// <para>
/// Compiled only under an Android target framework (e.g. <c>net10.0-android</c>),
/// which auto-defines the <c>ANDROID</c> symbol.
/// </para>
/// <para>
/// Scanning uses <see cref="BluetoothLeScanner"/>. Connection and GATT operations go through
/// <see cref="BluetoothGatt"/> with a custom <see cref="BluetoothGattCallback"/>.
/// </para>
/// <para>
/// <b>Permissions required</b> (declare in <c>AndroidManifest.xml</c>):
/// <c>BLUETOOTH_SCAN</c>, <c>BLUETOOTH_CONNECT</c>, <c>ACCESS_FINE_LOCATION</c> (API &lt; 31).
/// </para>
/// </remarks>
public sealed class AndroidBleBackend : IBleBackend
{
    private BluetoothAdapter? GetAdapter()
    {
        var manager = global::Android.App.Application.Context.GetSystemService(Context.BluetoothService) as BluetoothManager;
        return manager?.Adapter;
    }

    /// <inheritdoc />
    public Task<bool> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var adapter = GetAdapter();
        bool available = adapter is { IsEnabled: true };
        Debug.WriteLine($"[AndroidBLE] Adapter available={available}");
        return Task.FromResult(available);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BleDiscoveredDevice>> ScanAsync(int timeMs, CancellationToken cancellationToken = default)
    {
        var adapter = GetAdapter();
        if (adapter?.BluetoothLeScanner is not { } scanner)
        {
            Debug.WriteLine("[AndroidBLE] No LE scanner available.");
            return [];
        }

        var devices = new ConcurrentDictionary<string, (string Address, string Name, BluetoothDevice Device)>();

        var callback = new ScanCallback(result =>
        {
            if (result?.Device is null) return;

            var addr = result.Device.Address ?? "";
            var name = result.Device.Name ?? result.ScanRecord?.DeviceName ?? addr;
            devices[addr] = (addr, name, result.Device);
        });

        Debug.WriteLine($"[AndroidBLE] Starting scan ({timeMs} ms)...");
        scanner.StartScan(callback);
        try
        {
            await Task.Delay(timeMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            scanner.StopScan(callback);
        }

        var discovered = devices.Values
            .Select(d => new BleDiscoveredDevice
            {
                Id = d.Address,
                Name = d.Name,
                Peripheral = new AndroidBlePeripheral(d.Device, d.Name)
            })
            .ToList();

        Debug.WriteLine($"[AndroidBLE] Scan complete — {discovered.Count} device(s) found.");
        return discovered;
    }

    #region ScanCallback

    private sealed class ScanCallback : global::Android.Bluetooth.LE.ScanCallback
    {
        private readonly Action<ScanResult?> _onResult;

        public ScanCallback(Action<ScanResult?> onResult) => _onResult = onResult;

        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
            => _onResult(result);

        public override void OnScanFailed(ScanFailure errorCode)
            => Debug.WriteLine($"[AndroidBLE] Scan failed: {errorCode}");
    }

    #endregion

    #region AndroidBlePeripheral

    private sealed class AndroidBlePeripheral : IBlePeripheral
    {
        private readonly BluetoothDevice _device;
        private GattCallbackHandler? _gattHandler;
        private BluetoothGatt? _gatt;
        private readonly ConcurrentDictionary<(Guid, Guid), AndroidBleCharacteristic> _characteristics = new();

        public string Id => _device.Address ?? "";
        public string Name { get; }
        public bool IsConnected => _gattHandler?.IsConnected ?? false;

        public AndroidBlePeripheral(BluetoothDevice device, string name)
        {
            _device = device;
            Name = name;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (IsConnected) return;

            Debug.WriteLine($"[AndroidBLE] Connecting to {Name} ({Id})...");

            await DisconnectAsync().ConfigureAwait(false);
            _gattHandler = new GattCallbackHandler();
            _gatt = _device.ConnectGatt(
                global::Android.App.Application.Context,
                autoConnect: false,
                _gattHandler,
                BluetoothTransports.Le);

            try
            {
                if (_gatt is null)
                    throw new InvalidOperationException("Android could not create a Bluetooth GATT connection.");
                await _gattHandler.WaitForServicesDiscoveredAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await DisconnectAsync().ConfigureAwait(false);
                throw;
            }
            Debug.WriteLine($"[AndroidBLE] Connected to {Name}, services discovered.");
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _gattHandler?.Fail(new InvalidOperationException("Bluetooth device disconnected."));
            if (_gatt is not null)
            {
                Debug.WriteLine($"[AndroidBLE] Disconnecting {Name}...");
                _gatt.Disconnect();
                _gatt.Close();
                _gatt = null;
            }

            _gattHandler = null;
            _characteristics.Clear();
            return Task.CompletedTask;
        }

        public Task<IBleCharacteristic?> GetCharacteristicAsync(
            Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default)
        {
            var key = (serviceUuid, characteristicUuid);
            if (_characteristics.TryGetValue(key, out var cached))
                return Task.FromResult<IBleCharacteristic?>(cached);

            if (_gatt is null || _gattHandler is null || !IsConnected)
            {
                Debug.WriteLine("[AndroidBLE] Not connected — cannot get characteristic.");
                return Task.FromResult<IBleCharacteristic?>(null);
            }

            var service = _gatt.GetService(Java.Util.UUID.FromString(serviceUuid.ToString()));
            if (service is null)
            {
                Debug.WriteLine($"[AndroidBLE] Service {serviceUuid} not found.");
                return Task.FromResult<IBleCharacteristic?>(null);
            }

            var characteristic = service.GetCharacteristic(Java.Util.UUID.FromString(characteristicUuid.ToString()));
            if (characteristic is null)
            {
                Debug.WriteLine($"[AndroidBLE] Characteristic {characteristicUuid} not found.");
                return Task.FromResult<IBleCharacteristic?>(null);
            }

            var wrapper = new AndroidBleCharacteristic(_gatt, _gattHandler, characteristic);
            _characteristics.TryAdd(key, wrapper);
            return Task.FromResult<IBleCharacteristic?>(wrapper);
        }
    }

    #endregion

    #region GattCallbackHandler

    private sealed class GattCallbackHandler : BluetoothGattCallback
    {
        private readonly TaskCompletionSource<bool> _servicesTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<Guid, Action<byte[]>> _notificationHandlers = new();
        private long _receivedUpdates;
        public GattOperationQueue Writes { get; } = new();

        public bool IsConnected { get; private set; }

        public Task WaitForServicesDiscoveredAsync(CancellationToken ct)
            => _servicesTcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

        public void RegisterNotificationHandler(Guid characteristicUuid, Action<byte[]> handler)
            => _notificationHandlers[characteristicUuid] = handler;

        public void UnregisterNotificationHandler(Guid characteristicUuid)
            => _notificationHandlers.TryRemove(characteristicUuid, out _);

        public void Fail(Exception error)
        {
            Log.Info("AndroidBLE", $"Closing GATT session after {Interlocked.Read(ref _receivedUpdates)} data updates: {error.Message}");
            IsConnected = false;
            _servicesTcs.TrySetException(error);
            Writes.Fail(error);
            _notificationHandlers.Clear();
        }

        public static string WriteKey(BluetoothGattCharacteristic characteristic)
            => $"characteristic:{characteristic.Service?.Uuid}/{characteristic.Uuid}";

        public static string WriteKey(BluetoothGattDescriptor descriptor)
            => $"descriptor:{descriptor.Characteristic?.Service?.Uuid}/{descriptor.Characteristic?.Uuid}/{descriptor.Uuid}";

        public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
        {
            Log.Info("AndroidBLE", $"Command write completed: {characteristic?.Uuid}, status={status}.");
            if (characteristic is not null) Writes.Complete(WriteKey(characteristic), (int)status);
        }

        public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status)
        {
            Log.Info("AndroidBLE", $"Subscription write completed: {descriptor?.Characteristic?.Uuid}, status={status}.");
            if (descriptor is not null) Writes.Complete(WriteKey(descriptor), (int)status);
        }

        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            Debug.WriteLine($"[AndroidBLE] ConnectionStateChange: {newState} (status={status})");
            IsConnected = status == GattStatus.Success && newState == ProfileState.Connected;

            if (IsConnected)
            {
                if (gatt?.DiscoverServices() != true)
                    Fail(new InvalidOperationException("Android could not start Bluetooth service discovery."));
            }
            else
                Fail(new InvalidOperationException($"Bluetooth disconnected (GATT status {status})."));
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            Debug.WriteLine($"[AndroidBLE] ServicesDiscovered (status={status})");
            if (status == GattStatus.Success)
                _servicesTcs.TrySetResult(true);
            else
                Fail(new InvalidOperationException($"Bluetooth service discovery failed (GATT status {status})."));
        }

        public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value)
            => DeliverUpdate(characteristic, value);

        private void DeliverUpdate(BluetoothGattCharacteristic characteristic, byte[] value)
        {
            var uuid = Guid.Parse(characteristic.Uuid?.ToString() ?? "");
            bool registered = _notificationHandlers.TryGetValue(uuid, out var handler);
            if (Interlocked.Increment(ref _receivedUpdates) == 1)
            {
                Log.Info("AndroidBLE", $"First data update: {uuid}, {value.Length} bytes, handler registered={registered}.");
                Console.WriteLine($"[AndroidBLE] First data update: {uuid}, {value.Length} bytes, handler registered={registered}.");
            }
            handler?.Invoke(value);
        }

#pragma warning disable CS0618 // Fallback for older Android APIs
        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
        {
            if (characteristic?.GetValue() is not { Length: > 0 } data) return;
            DeliverUpdate(characteristic, data);
        }
#pragma warning restore CS0618
    }

    #endregion

    #region AndroidBleCharacteristic

    private sealed class AndroidBleCharacteristic : IBleCharacteristic
    {
        private readonly BluetoothGatt _gatt;
        private readonly GattCallbackHandler _callbackHandler;
        private readonly BluetoothGattCharacteristic _characteristic;
        private static readonly Guid CccdUuid = Guid.Parse("00002902-0000-1000-8000-00805f9b34fb");

        public Guid Uuid => Guid.Parse(_characteristic.Uuid?.ToString() ?? "");
        public event EventHandler<byte[]>? ValueChanged;

        public AndroidBleCharacteristic(BluetoothGatt gatt, GattCallbackHandler callbackHandler,
            BluetoothGattCharacteristic characteristic)
        {
            _gatt = gatt;
            _callbackHandler = callbackHandler;
            _characteristic = characteristic;
        }

        public Task WriteValueWithoutResponseAsync(byte[] value, CancellationToken cancellationToken = default)
            => WriteAsync(value, preferResponse: false, cancellationToken);

        public Task WriteValueAsync(byte[] value, CancellationToken cancellationToken = default)
            => WriteAsync(value, preferResponse: true, cancellationToken);

        private Task WriteAsync(byte[] value, bool preferResponse, CancellationToken cancellationToken)
        {
            var properties = _characteristic.Properties;
            var writeType = GattWriteConfiguration.UseResponse(
                properties.HasFlag(GattProperty.Write), properties.HasFlag(GattProperty.WriteNoResponse), preferResponse)
                ? GattWriteType.Default : GattWriteType.NoResponse;
            return _callbackHandler.Writes.ExecuteAsync(GattCallbackHandler.WriteKey(_characteristic), () =>
            {
                Log.Info("AndroidBLE", $"Writing {Uuid}: properties={properties}, writeType={writeType}, length={value.Length}.");
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                    return _gatt.WriteCharacteristic(_characteristic, value, (int)writeType) == 0;
#pragma warning disable CS0618
                _characteristic.WriteType = writeType;
                _characteristic.SetValue(value);
                return _gatt.WriteCharacteristic(_characteristic);
#pragma warning restore CS0618
            }, cancellationToken);
        }

        public async Task StartNotificationsAsync(CancellationToken cancellationToken = default)
        {
            var properties = _characteristic.Properties;
            var value = GattSubscriptionConfiguration.Select(
                properties.HasFlag(GattProperty.Notify), properties.HasFlag(GattProperty.Indicate));
            Log.Info("AndroidBLE", $"Subscribing to {Uuid}: properties={properties}, CCCD={Convert.ToHexString(value)}.");
            Console.WriteLine($"[AndroidBLE] Subscribing to {Uuid}: properties={properties}, CCCD={Convert.ToHexString(value)}.");
            var descriptor = _characteristic.GetDescriptor(Java.Util.UUID.FromString(CccdUuid.ToString()))
                ?? throw new InvalidOperationException($"Notification descriptor missing for {Uuid}.");
            _callbackHandler.RegisterNotificationHandler(Uuid, data => ValueChanged?.Invoke(this, data));
            try
            {
                await _callbackHandler.Writes.ExecuteAsync(GattCallbackHandler.WriteKey(descriptor), () =>
                {
                    if (!_gatt.SetCharacteristicNotification(_characteristic, true)) return false;
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                        return _gatt.WriteDescriptor(descriptor, value) == 0;
#pragma warning disable CS0618
                    descriptor.SetValue(value);
                    return _gatt.WriteDescriptor(descriptor);
#pragma warning restore CS0618
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _callbackHandler.UnregisterNotificationHandler(Uuid);
                _gatt.SetCharacteristicNotification(_characteristic, false);
                throw;
            }

            Console.WriteLine($"[AndroidBLE] Notifications confirmed for {Uuid}");
        }
    }

    #endregion
}
#endif
