namespace MOSAIC.Components.Manager.BLE;

public sealed class BleDiscoveredDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IBlePeripheral Peripheral { get; init; }
}