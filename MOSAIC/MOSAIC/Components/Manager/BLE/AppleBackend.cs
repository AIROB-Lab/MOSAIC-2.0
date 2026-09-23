#if APPLE_BLE
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreBluetooth;
using CoreFoundation;
using Foundation;
using MOSAIC.Diagnostics;

namespace MOSAIC.Components.Manager.BLE;

/// <summary>
/// BLE backend for Apple platforms (macOS and iOS) using the native CoreBluetooth framework.
/// </summary>
/// <remarks>
/// <para>
/// This backend is compiled only when the <c>APPLE_BLE</c> preprocessor symbol is defined
/// (e.g. in the <c>.csproj</c> for net10.0-macos / net10.0-ios target frameworks).
/// </para>
/// <para>
/// CoreBluetooth delegates are event-driven; this backend converts them to async/await
/// via <see cref="TaskCompletionSource{TResult}"/> waiters stored in concurrent dictionaries.
/// </para>
/// </remarks>
public sealed class AppleBleBackend : IBleBackend
{
    private readonly CentralManagerDelegate _delegate;
    private readonly CBCentralManager _centralManager;
    private readonly ConcurrentDictionary<Guid, AppleBlePeripheral> _peripherals = new();

    public AppleBleBackend()
    {
        _delegate = new CentralManagerDelegate();
        _centralManager = new CBCentralManager(_delegate, DispatchQueue.MainQueue);
        _delegate.Attach(_centralManager);
    }

    /// <inheritdoc />
    public async Task<bool> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var state = await _delegate.WaitForStateAsync(cancellationToken).ConfigureAwait(false);
        return state == CBManagerState.PoweredOn;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BleDiscoveredDevice>> ScanAsync(int timeMs, CancellationToken cancellationToken = default)
    {
        var state = await _delegate.WaitForStateAsync(cancellationToken).ConfigureAwait(false);
        if (state != CBManagerState.PoweredOn)
            return [];

        _delegate.ClearScanResults();
        _centralManager.ScanForPeripherals(null, new PeripheralScanningOptions { AllowDuplicatesKey = false });
        try
        {
            await Task.Delay(timeMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _centralManager.StopScan();
        }

        return _delegate.GetScanResults()
            .Select(peripheral =>
            {
                var wrapper = _peripherals.GetOrAdd(
                    peripheral.Identifier.AsGuid(),
                    _ => new AppleBlePeripheral(_centralManager, _delegate, peripheral));
                return new BleDiscoveredDevice
                {
                    Id = wrapper.Id,
                    Name = wrapper.Name,
                    Peripheral = wrapper
                };
            })
            .ToList();
    }

    #region CentralManagerDelegate

    private sealed class CentralManagerDelegate : CBCentralManagerDelegate
    {
        private readonly object _scanLock = new();
        private readonly Dictionary<Guid, CBPeripheral> _scanResults = [];
        private TaskCompletionSource<CBManagerState>? _stateTcs;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<CBPeripheral>> _connectWaiters = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _disconnectWaiters = new();
        private CBCentralManager? _centralManager;

        public void Attach(CBCentralManager centralManager)
        {
            _centralManager = centralManager;
        }

        public Task<CBManagerState> WaitForStateAsync(CancellationToken cancellationToken)
        {
            if (_centralManager is null)
                throw new InvalidOperationException("Central manager is not initialised.");

            if (_centralManager.State != CBManagerState.Unknown &&
                _centralManager.State != CBManagerState.Resetting)
                return Task.FromResult(_centralManager.State);

            var tcs = new TaskCompletionSource<CBManagerState>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stateTcs = tcs;
            return tcs.Task.WaitAsync(cancellationToken);
        }

        public void ClearScanResults()
        {
            lock (_scanLock) _scanResults.Clear();
        }

        public IReadOnlyList<CBPeripheral> GetScanResults()
        {
            lock (_scanLock) return _scanResults.Values.ToList();
        }

        public Task WaitForConnectedAsync(CBPeripheral peripheral, CancellationToken cancellationToken)
        {
            if (peripheral.State == CBPeripheralState.Connected)
                return Task.CompletedTask;

            var tcs = _connectWaiters.GetOrAdd(
                peripheral.Identifier.AsGuid(),
                _ => new TaskCompletionSource<CBPeripheral>(TaskCreationOptions.RunContinuationsAsynchronously));
            return tcs.Task.WaitAsync(cancellationToken);
        }

        public Task WaitForDisconnectedAsync(CBPeripheral peripheral, CancellationToken cancellationToken)
        {
            if (peripheral.State == CBPeripheralState.Disconnected)
                return Task.CompletedTask;

            var tcs = _disconnectWaiters.GetOrAdd(
                peripheral.Identifier.AsGuid(),
                _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            return tcs.Task.WaitAsync(cancellationToken);
        }

        public override void UpdatedState(CBCentralManager central)
        {
            if (central.State == CBManagerState.Unsupported)
                _stateTcs?.TrySetException(new PlatformNotSupportedException(
                    "Bluetooth Low Energy is not supported on this device."));
            else if (central.State == CBManagerState.Unauthorized)
                _stateTcs?.TrySetException(new UnauthorizedAccessException(
                    "Bluetooth access is not authorised for this app."));
            else
                _stateTcs?.TrySetResult(central.State);
        }

        public override void DiscoveredPeripheral(CBCentralManager central, CBPeripheral peripheral,
            NSDictionary advertisementData, NSNumber RSSI)
        {
            lock (_scanLock) _scanResults[peripheral.Identifier.AsGuid()] = peripheral;
        }

        public override void ConnectedPeripheral(CBCentralManager central, CBPeripheral peripheral)
        {
            if (_connectWaiters.TryRemove(peripheral.Identifier.AsGuid(), out var tcs))
                tcs.TrySetResult(peripheral);
        }

        public override void FailedToConnectPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError error)
        {
            if (_connectWaiters.TryRemove(peripheral.Identifier.AsGuid(), out var tcs))
                tcs.TrySetException(new InvalidOperationException(
                    error?.LocalizedDescription ?? $"Failed to connect to {peripheral.Name}."));
        }

        public override void DisconnectedPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error)
        {
            if (_disconnectWaiters.TryRemove(peripheral.Identifier.AsGuid(), out var tcs))
            {
                if (error is null) tcs.TrySetResult(true);
                else tcs.TrySetException(new InvalidOperationException(error.LocalizedDescription));
            }
        }
    }

    #endregion

    #region AppleBlePeripheral

    private sealed class AppleBlePeripheral : IBlePeripheral
    {
        private readonly CBCentralManager _centralManager;
        private readonly CentralManagerDelegate _centralDelegate;
        private readonly CBPeripheral _peripheral;
        private readonly PeripheralDelegate _delegate;
        private readonly ConcurrentDictionary<(Guid Service, Guid Characteristic), AppleBleCharacteristic> _characteristics = new();

        public AppleBlePeripheral(CBCentralManager centralManager, CentralManagerDelegate centralDelegate, CBPeripheral peripheral)
        {
            _centralManager = centralManager;
            _centralDelegate = centralDelegate;
            _peripheral = peripheral;
            _delegate = new PeripheralDelegate(peripheral);
            _peripheral.Delegate = _delegate;
        }

        public string Id => _peripheral.Identifier.AsString();
        public string Name => string.IsNullOrWhiteSpace(_peripheral.Name) ? Id : _peripheral.Name;
        public bool IsConnected => _peripheral.State == CBPeripheralState.Connected;

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (IsConnected) return;
            _centralManager.ConnectPeripheral(_peripheral);
            await _centralDelegate.WaitForConnectedAsync(_peripheral, cancellationToken).ConfigureAwait(false);
            _peripheral.Delegate = _delegate;
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected) return;
            _centralManager.CancelPeripheralConnection(_peripheral);
            await _centralDelegate.WaitForDisconnectedAsync(_peripheral, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IBleCharacteristic?> GetCharacteristicAsync(Guid serviceUuid, Guid characteristicUuid,
            CancellationToken cancellationToken = default)
        {
            var key = (serviceUuid, characteristicUuid);
            if (_characteristics.TryGetValue(key, out var existing))
                return existing;

            if (!IsConnected)
                await ConnectAsync(cancellationToken).ConfigureAwait(false);

            var service = await _delegate
                .GetServiceAsync(CBUUID.FromString(serviceUuid.ToString()), cancellationToken)
                .ConfigureAwait(false);
            if (service is null) return null;

            var characteristic = await _delegate
                .GetCharacteristicAsync(service, CBUUID.FromString(characteristicUuid.ToString()), cancellationToken)
                .ConfigureAwait(false);
            if (characteristic is null) return null;

            return _characteristics.GetOrAdd(key,
                _ => new AppleBleCharacteristic(_peripheral, _delegate, characteristic));
        }
    }

    #endregion

    #region PeripheralDelegate

    private sealed class PeripheralDelegate : CBPeripheralDelegate
    {
        private readonly CBPeripheral _peripheral;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<CBService[]?>> _serviceWaiters = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<CBCharacteristic[]?>> _characteristicWaiters = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _notifyWaiters = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _writeWaiters = new();
        private readonly ConcurrentDictionary<Guid, Action<byte[]>> _notificationHandlers = new();

        public PeripheralDelegate(CBPeripheral peripheral) => _peripheral = peripheral;

        public async Task<CBService?> GetServiceAsync(CBUUID serviceUuid, CancellationToken cancellationToken)
        {
            if (_peripheral.Services?.FirstOrDefault(s => UuidEquals(s.UUID, serviceUuid)) is { } existing)
                return existing;

            var key = serviceUuid.AsGuid();
            var tcs = _serviceWaiters.GetOrAdd(key,
                _ => new TaskCompletionSource<CBService[]?>(TaskCreationOptions.RunContinuationsAsynchronously));
            _peripheral.DiscoverServices([serviceUuid]);
            var services = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return services?.FirstOrDefault(s => UuidEquals(s.UUID, serviceUuid));
        }

        public async Task<CBCharacteristic?> GetCharacteristicAsync(CBService service, CBUUID characteristicUuid,
            CancellationToken cancellationToken)
        {
            if (service.Characteristics?.FirstOrDefault(c => UuidEquals(c.UUID, characteristicUuid)) is { } existing)
                return existing;

            var key = characteristicUuid.AsGuid();
            var tcs = _characteristicWaiters.GetOrAdd(key,
                _ => new TaskCompletionSource<CBCharacteristic[]?>(TaskCreationOptions.RunContinuationsAsynchronously));
            _peripheral.DiscoverCharacteristics([characteristicUuid], service);
            var characteristics = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return characteristics?.FirstOrDefault(c => UuidEquals(c.UUID, characteristicUuid));
        }

        public async Task StartNotificationsAsync(CBCharacteristic characteristic, CancellationToken cancellationToken)
        {
            if (characteristic.IsNotifying) return;

            var key = characteristic.UUID.AsGuid();
            var tcs = _notifyWaiters.GetOrAdd(key,
                _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            _peripheral.SetNotifyValue(true, characteristic);
            await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteValueAsync(CBCharacteristic characteristic, byte[] value,
            bool preferResponse, CancellationToken cancellationToken)
        {
            var writeType = GattWriteConfiguration.UseResponse(
                characteristic.Properties.HasFlag(CBCharacteristicProperties.Write),
                characteristic.Properties.HasFlag(CBCharacteristicProperties.WriteWithoutResponse), preferResponse)
                ? CBCharacteristicWriteType.WithResponse : CBCharacteristicWriteType.WithoutResponse;
            Log.Info("AppleBLE", $"Writing {characteristic.UUID}: properties={characteristic.Properties}, writeType={writeType}, length={value.Length}.");

            if (writeType == CBCharacteristicWriteType.WithoutResponse)
            {
                _peripheral.WriteValue(NSData.FromArray(value), characteristic, writeType);
                return;
            }

            var key = characteristic.UUID.AsGuid();
            var tcs = _writeWaiters.GetOrAdd(key,
                _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            _peripheral.WriteValue(NSData.FromArray(value), characteristic, writeType);
            await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void RegisterNotificationHandler(CBCharacteristic characteristic, Action<byte[]> handler)
            => _notificationHandlers[characteristic.UUID.AsGuid()] = handler;

        public override void DiscoveredService(CBPeripheral peripheral, NSError? error)
        {
            if (error is not null)
            {
                foreach (var waiter in _serviceWaiters.Values)
                    waiter.TrySetException(new InvalidOperationException(error.LocalizedDescription));
                return;
            }
            foreach (var service in peripheral.Services ?? [])
            {
                if (_serviceWaiters.TryRemove(service.UUID.AsGuid(), out var tcs))
                    tcs.TrySetResult(peripheral.Services);
            }
        }

        public override void DiscoveredCharacteristics(CBPeripheral peripheral, CBService service, NSError? error)
        {
            if (error is not null)
            {
                foreach (var waiter in _characteristicWaiters.Values)
                    waiter.TrySetException(new InvalidOperationException(error.LocalizedDescription));
                return;
            }
            foreach (var characteristic in service.Characteristics ?? [])
            {
                if (_characteristicWaiters.TryRemove(characteristic.UUID.AsGuid(), out var tcs))
                    tcs.TrySetResult(service.Characteristics);
            }
        }

        public override void UpdatedCharacterteristicValue(CBPeripheral peripheral, CBCharacteristic characteristic,
            NSError? error)
        {
            if (error is not null) return;
            if (_notificationHandlers.TryGetValue(characteristic.UUID.AsGuid(), out var handler) &&
                characteristic.Value is not null)
                handler(characteristic.Value.ToArray());
        }

        public override void UpdatedNotificationState(CBPeripheral peripheral, CBCharacteristic characteristic,
            NSError? error)
        {
            if (!_notifyWaiters.TryRemove(characteristic.UUID.AsGuid(), out var tcs)) return;
            if (error is null) tcs.TrySetResult(true);
            else tcs.TrySetException(new InvalidOperationException(error.LocalizedDescription));
        }

        public override void WroteCharacteristicValue(CBPeripheral peripheral, CBCharacteristic characteristic,
            NSError? error)
        {
            Log.Info("AppleBLE", $"Command write completed: {characteristic.UUID}, error={error?.LocalizedDescription ?? "none"}.");
            if (!_writeWaiters.TryRemove(characteristic.UUID.AsGuid(), out var tcs)) return;
            if (error is null) tcs.TrySetResult(true);
            else tcs.TrySetException(new InvalidOperationException(error.LocalizedDescription));
        }

        private static bool UuidEquals(CBUUID left, CBUUID right)
            => string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region AppleBleCharacteristic

    private sealed class AppleBleCharacteristic : IBleCharacteristic
    {
        private readonly CBPeripheral _peripheral;
        private readonly PeripheralDelegate _delegate;
        private readonly CBCharacteristic _characteristic;

        public AppleBleCharacteristic(CBPeripheral peripheral, PeripheralDelegate @delegate, CBCharacteristic characteristic)
        {
            _peripheral = peripheral;
            _delegate = @delegate;
            _characteristic = characteristic;
        }

        public Guid Uuid => _characteristic.UUID.AsGuid();
        public event EventHandler<byte[]>? ValueChanged;

        public Task WriteValueWithoutResponseAsync(byte[] value, CancellationToken cancellationToken = default)
            => _delegate.WriteValueAsync(_characteristic, value, preferResponse: false, cancellationToken);

        public Task WriteValueAsync(byte[] value, CancellationToken cancellationToken = default)
            => _delegate.WriteValueAsync(_characteristic, value, preferResponse: true, cancellationToken);

        public async Task StartNotificationsAsync(CancellationToken cancellationToken = default)
        {
            _delegate.RegisterNotificationHandler(_characteristic, data => ValueChanged?.Invoke(_peripheral, data));
            await _delegate.StartNotificationsAsync(_characteristic, cancellationToken).ConfigureAwait(false);
        }
    }

    #endregion
}

#region Extension helpers

internal static class NSUuidExtensions
{
    public static Guid AsGuid(this NSUuid uuid)
        => Guid.TryParse(uuid.AsString(), out var guid) ? guid : Guid.Empty;
}

internal static class CBUuidExtensions
{
    public static Guid AsGuid(this CBUUID uuid)
    {
        var value = uuid.ToString();
        return Guid.TryParse(value, out var guid) ? guid : Guid.Empty;
    }
}

#endregion
#endif
