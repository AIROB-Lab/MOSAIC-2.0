#if WINDOWS10_0_19041_0_OR_GREATER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using MOSAIC.Diagnostics;

namespace MOSAIC.Components.Manager.BLE;

/// <summary>
/// Native Windows BLE backend using WinRT <c>Windows.Devices.Bluetooth</c> APIs.
/// </summary>
public sealed class WindowsBleBackend : IBleBackend
{
    /// <inheritdoc />
    public async Task<bool> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync().AsTask(cancellationToken);
            var btRadio = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth);
            if (btRadio is null)
            {
                Log.Warn("WindowsBLE", "No Bluetooth radio found.");
                return false;
            }

            bool on = btRadio.State == Windows.Devices.Radios.RadioState.On;
            Console.WriteLine($"[WindowsBLE] Radio state: {btRadio.State} → available={on}");
            return on;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowsBLE] GetAvailabilityAsync error: {ex.Message}");
            try
            {
                var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                var paired = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken);
                Console.WriteLine($"[WindowsBLE] Fallback: found {paired.Count} paired BLE device(s) — assuming available.");
                return true;
            }
            catch (Exception fallbackEx)
            {
                // Without this the caller reports a plain "Bluetooth not available" and the real
                // reason - the radio query AND the paired-device fallback both failing - is lost.
                Log.Warn("WindowsBLE", fallbackEx, "Paired-device fallback failed; reporting Bluetooth as unavailable.");
                return false;
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BleDiscoveredDevice>> ScanAsync(int timeMs, CancellationToken cancellationToken = default)
    {
        var devices = new ConcurrentDictionary<ulong, (string Id, string Name, ulong Address)>();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += async (_, args) =>
        {
            var addr = args.BluetoothAddress;
            var id = addr.ToString("X12");
            var name = args.Advertisement.LocalName;

            if (string.IsNullOrEmpty(name))
            {
                if (devices.TryGetValue(addr, out var existing) && existing.Name != existing.Id)
                    return;

                try
                {
                    using var bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                    if (bleDevice is not null)
                        name = bleDevice.Name;
                }
                catch { /* ignore */ }
            }

            if (string.IsNullOrEmpty(name))
                name = id;

            devices[addr] = (id, name, addr);
        };

        Console.WriteLine($"[WindowsBLE] Starting scan ({timeMs} ms)...");
        watcher.Start();
        try
        {
            await Task.Delay(timeMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            watcher.Stop();
        }

        var result = devices.Values
            .Select(d => new BleDiscoveredDevice
            {
                Id = d.Id,
                Name = d.Name,
                Peripheral = new WindowsBlePeripheral(d.Address, d.Name)
            })
            .ToList();

        Console.WriteLine($"[WindowsBLE] Scan complete — {result.Count} device(s) found.");
        return result;
    }

    #region WindowsBlePeripheral

    private sealed class WindowsBlePeripheral : IBlePeripheral
    {
        private readonly ulong _address;
        private BluetoothLEDevice? _device;
        private GattSession? _session;
        private readonly ConcurrentDictionary<(Guid, Guid), WindowsBleCharacteristic> _characteristics = new();

        public string Id { get; }
        public string Name { get; }

        public WindowsBlePeripheral(ulong address, string name)
        {
            _address = address;
            Id = address.ToString("X12");
            Name = name;
        }

        public bool IsConnected
        {
            get
            {
                if (_device is null) return false;
                return _device.ConnectionStatus == BluetoothConnectionStatus.Connected;
            }
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (IsConnected) return;

            Console.WriteLine($"[WindowsBLE] Connecting to {Name} ({Id})...");
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_address)
                .AsTask(cancellationToken).ConfigureAwait(false);

            if (_device is null)
                throw new InvalidOperationException($"Could not create BLE device for address {Id}.");

            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId)
                .AsTask(cancellationToken).ConfigureAwait(false);
            _session.MaintainConnection = true;

            Console.WriteLine($"[WindowsBLE] GattSession created, MaintainConnection=true");

            // Force GATT service discovery — this triggers the actual BLE connection
            var result = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"[WindowsBLE] GATT result: {result.Status}, services={result.Services.Count}, connected={_device.ConnectionStatus}");

            if (result.Status != GattCommunicationStatus.Success)
            {
                _session.Dispose();
                _session = null;
                _device.Dispose();
                _device = null;
                throw new InvalidOperationException($"Failed to connect to {Name}: {result.Status}");
            }

            Console.WriteLine($"[WindowsBLE] Connected to {Name}, {result.Services.Count} service(s).");
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (_session is not null)
            {
                _session.Dispose();
                _session = null;
            }
            if (_device is not null)
            {
                Console.WriteLine($"[WindowsBLE] Disconnecting {Name}...");
                _device.Dispose();
                _device = null;
            }

            _characteristics.Clear();
            return Task.CompletedTask;
        }

        public async Task<IBleCharacteristic?> GetCharacteristicAsync(
            Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default)
        {
            var key = (serviceUuid, characteristicUuid);
            if (_characteristics.TryGetValue(key, out var cached))
                return cached;

            if (_device is null)
                await ConnectAsync(cancellationToken).ConfigureAwait(false);

            if (_device is null) return null;

            Console.WriteLine($"[WindowsBLE] GetCharacteristic: svc={serviceUuid}, char={characteristicUuid}");

            var svcResult = await _device
                .GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken).ConfigureAwait(false);

            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            {
                Log.Error("WindowsBLE", $"Service {serviceUuid} not found (status={svcResult.Status}).");
                return null;
            }

            var service = svcResult.Services[0];

            var charResult = await service
                .GetCharacteristicsForUuidAsync(characteristicUuid, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken).ConfigureAwait(false);

            if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
            {
                Log.Error("WindowsBLE", $"Characteristic {characteristicUuid} not found (status={charResult.Status}).");
                return null;
            }

            Console.WriteLine($"[WindowsBLE] Found characteristic {characteristicUuid}");
            var characteristic = new WindowsBleCharacteristic(charResult.Characteristics[0]);
            return _characteristics.GetOrAdd(key, _ => characteristic);
        }
    }

    #endregion

    #region WindowsBleCharacteristic

    private sealed class WindowsBleCharacteristic : IBleCharacteristic
    {
        private readonly GattCharacteristic _characteristic;

        public Guid Uuid => _characteristic.Uuid;
        public event EventHandler<byte[]>? ValueChanged;

        public WindowsBleCharacteristic(GattCharacteristic characteristic)
        {
            _characteristic = characteristic;
        }

        public async Task WriteValueWithoutResponseAsync(byte[] value, CancellationToken cancellationToken = default)
        {
            var buffer = value.AsBuffer();
            var writeOption = _characteristic.CharacteristicProperties.HasFlag(
                GattCharacteristicProperties.WriteWithoutResponse)
                ? GattWriteOption.WriteWithoutResponse
                : GattWriteOption.WriteWithResponse;

            var result = await _characteristic.WriteValueAsync(buffer, writeOption)
                .AsTask(cancellationToken).ConfigureAwait(false);

            if (result != GattCommunicationStatus.Success)
                Log.Error("WindowsBLE", $"Write FAILED: {result} for {Uuid}");
            else
                Console.WriteLine($"[WindowsBLE] Write OK for {Uuid}");
        }

        public async Task StartNotificationsAsync(CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"[WindowsBLE] StartNotifications for {Uuid}, props={_characteristic.CharacteristicProperties}");

            var cccdValue = _characteristic.CharacteristicProperties.HasFlag(
                GattCharacteristicProperties.Indicate)
                ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                : GattClientCharacteristicConfigurationDescriptorValue.Notify;

            var status = await _characteristic
                .WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue)
                .AsTask(cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"[WindowsBLE] CCCD write result: {status} for {Uuid}");

            if (status != GattCommunicationStatus.Success)
            {
                Log.Error("WindowsBLE", $"Failed to enable notifications: {status}");
                return;
            }

            _characteristic.ValueChanged += OnValueChanged;
            Console.WriteLine($"[WindowsBLE] Notifications enabled and handler attached for {Uuid}");
        }

        private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var data = args.CharacteristicValue.ToArray();
            if (data.Length > 0)
                ValueChanged?.Invoke(this, data);
        }
    }

    #endregion
}
#endif