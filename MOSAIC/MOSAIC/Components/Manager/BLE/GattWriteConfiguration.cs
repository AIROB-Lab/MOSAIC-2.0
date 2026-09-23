using System;

namespace MOSAIC.Components.Manager.BLE;

internal static class GattWriteConfiguration
{
    /// <summary>
    /// Control commands should request a remote acknowledgement when supported.
    /// Never issue a write type that the characteristic does not advertise: an ATT
    /// write command (no response) can otherwise be silently ignored by the server.
    /// </summary>
    internal static bool UseResponse(bool supportsResponse, bool supportsNoResponse, bool preferResponse)
    {
        if (supportsResponse && (preferResponse || !supportsNoResponse)) return true;
        if (supportsNoResponse) return false;
        throw new InvalidOperationException("The Bluetooth characteristic does not support writing.");
    }
}
