using System;

namespace MOSAIC.Components.Manager.BLE;

/// <summary>Selects the remote CCCD value from the characteristic's advertised capabilities.</summary>
internal static class GattSubscriptionConfiguration
{
    public static byte[] Select(bool supportsNotifications, bool supportsIndications)
    {
        // Some Myo firmware streams via indications. Match the working Windows backend:
        // prefer acknowledged indications when available, rather than always writing Notify.
        if (supportsIndications) return [0x02, 0x00];
        if (supportsNotifications) return [0x01, 0x00];
        throw new InvalidOperationException("This Bluetooth characteristic supports neither notifications nor indications.");
    }
}
