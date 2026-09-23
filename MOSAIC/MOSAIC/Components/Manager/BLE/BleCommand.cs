namespace MOSAIC.Components.Manager.BLE;

public class BleCommand
{
    /// <summary>
    /// BLE service UUID string.
    /// </summary>
    public string Service { get; set; }

    /// <summary>
    /// BLE characteristic UUID string.
    /// </summary>
    public string Characteristic { get; set; }

    /// <summary>
    /// BLE command as byte array.
    /// </summary>
    public byte[] Cmd { get; set; }

    /// <summary>
    /// Constructor for BleCommand.
    /// </summary>
    /// <param name="service">The service associated with the BLE command.</param>
    /// <param name="characteristic">The characteristic associated with the BLE command.</param>
    /// <param name="cmd">The command data as a byte array. It is an optional parameter and can be null.</param>
    public BleCommand(string service, string characteristic, byte[]? cmd = null)
    {
        Service = service;
        Characteristic = characteristic;
        Cmd = cmd ?? [];
    }
}