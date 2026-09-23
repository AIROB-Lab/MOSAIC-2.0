using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MOSAIC.Components.Manager.BLE;

public interface IBleBackend
{
    Task<bool> GetAvailabilityAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BleDiscoveredDevice>> ScanAsync(int timeMs, CancellationToken cancellationToken = default);
}

public interface IBlePeripheral
{
    string Id { get; }
    string Name { get; }
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<IBleCharacteristic?> GetCharacteristicAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default);
}

public interface IBleCharacteristic
{
    Guid Uuid { get; }
    event EventHandler<byte[]>? ValueChanged;
    /// <summary>
    /// Writes a control value using the backend's supported write mode. Android and Apple
    /// prefer a remote acknowledgement; other backends retain their legacy write behavior.
    /// </summary>
    Task WriteValueAsync(byte[] value, CancellationToken cancellationToken = default)
        => WriteValueWithoutResponseAsync(value, cancellationToken);
    Task WriteValueWithoutResponseAsync(byte[] value, CancellationToken cancellationToken = default);
    Task StartNotificationsAsync(CancellationToken cancellationToken = default);
}
