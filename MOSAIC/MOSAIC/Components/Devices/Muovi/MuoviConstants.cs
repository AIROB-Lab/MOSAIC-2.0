namespace MOSAIC.Components.Devices.Muovi;

/// <summary>
/// Signal acquisition modes supported by OT Bioelettronica Muovi probes.
/// </summary>
/// <remarks>
/// <para>
/// The Muovi probe is a wireless HD-sEMG / EEG acquisition device manufactured by
/// <strong>OT Bioelettronica</strong> (Turin, Italy). Firmware documentation, protocol
/// specifications, and user manuals are available at
/// <see href="https://otbioelettronica.it/en/download/#55-171-wpfd-muovi"/>.
/// </para>
/// </remarks>
/// <seealso cref="MuoviConstants"/>
public enum MuoviSignalMode
{
    /// <summary>EMG mode — 2 000 Hz sample rate, gain ×8 (286.1 nV/bit, ±9.375 mV range).</summary>
    Emg,

    /// <summary>EMG low-gain mode — 2 000 Hz sample rate, gain ×4 (572.2 nV/bit, ±18.75 mV range).</summary>
    EmgLowGain,

    /// <summary>EEG mode — 500 Hz sample rate, EEG-optimised analogue front-end.</summary>
    Eeg
}

/// <summary>
/// Constants and static helper methods shared between <see cref="Models.Devices.MuoviSingleProbe"/> (direct Wi-Fi)
/// and <see cref="Devices.Muovi"/> (SyncStation multi-probe) blocks.
/// </summary>
/// <remarks>
/// <para>
/// All values are derived from the OT Bioelettronica Muovi protocol specification (v2.4 for
/// single-probe, v2.8 for SyncStation). Refer to the manufacturer documentation at
/// <see href="https://otbioelettronica.it/en/download/#55-171-wpfd-muovi"/> for full details.
/// </para>
/// </remarks>
public static class MuoviConstants
{

    /// <summary>Nominal sample rate in EMG mode (both standard and low-gain), in Hz.</summary>
    public const int NominalRateEmg = 2000;

    /// <summary>Nominal sample rate in EEG mode, in Hz.</summary>
    public const int NominalRateEeg = 500;
    

    /// <summary>Number of electrode channels per Muovi probe (4 × 8 grid).</summary>
    public const int ElectrodeChannels = 32;

    /// <summary>Number of IMU quaternion channels per probe (W, X, Y, Z).</summary>
    public const int ImuChannels = 4;

    /// <summary>
    /// Total raw columns per probe in a data frame: 32 electrode + 4 IMU + 2 accessory channels.
    /// </summary>
    public const int ChannelsPerProbeRaw = 38;
    

    /// <summary>
    /// Conversion factor for EMG standard gain mode (gain ×8).
    /// Multiply the raw <see cref="short"/> sample by this value to obtain normalised units.
    /// </summary>
    public const double EmgConversion = 1.0 / 32768;

    /// <summary>
    /// Conversion factor for EMG low-gain mode (gain ×4), in millivolts per bit.
    /// </summary>
    public const double EmgLowGainConversion = 0.000572;

    /// <summary>
    /// Conversion factor for EEG mode, in microvolts per bit.
    /// </summary>
    public const double EegConversion = 0.286;

    /// <summary>
    /// Returns the nominal sample rate for the specified <paramref name="mode"/>.
    /// </summary>
    /// <param name="mode">The signal acquisition mode.</param>
    /// <returns>2 000 for EMG modes, 500 for EEG.</returns>
    public static int NominalRate(MuoviSignalMode mode) =>
        mode == MuoviSignalMode.Eeg ? NominalRateEeg : NominalRateEmg;

    /// <summary>
    /// Returns the ADC-to-physical-unit conversion factor for the specified <paramref name="mode"/>.
    /// </summary>
    /// <param name="mode">The signal acquisition mode.</param>
    /// <returns>The conversion multiplier to apply to each raw <see cref="short"/> sample.</returns>
    public static double ConversionFactor(MuoviSignalMode mode) => mode switch
    {
        MuoviSignalMode.Emg        => EmgConversion,
        MuoviSignalMode.EmgLowGain => EmgLowGainConversion,
        MuoviSignalMode.Eeg        => EegConversion,
        _                          => EmgConversion
    };

    /// <summary>
    /// Builds the single-byte configuration value for the direct Wi-Fi protocol (single-probe mode, v2.4).
    /// </summary>
    /// <param name="mode">The desired signal acquisition mode.</param>
    /// <returns>
    /// A byte encoding <c>[0][0][0][0][EMG/EEG][MODE1][MODE0][GO=1]</c> as defined in the
    /// OT Bioelettronica single-probe protocol.
    /// </returns>
    /// <remarks>
    /// <list type="table">
    ///   <listheader><term>Mode</term><description>Config byte</description></listheader>
    ///   <item><term><see cref="MuoviSignalMode.Emg"/></term><description><c>0x09</c></description></item>
    ///   <item><term><see cref="MuoviSignalMode.EmgLowGain"/></term><description><c>0x0B</c></description></item>
    ///   <item><term><see cref="MuoviSignalMode.Eeg"/></term><description><c>0x01</c></description></item>
    /// </list>
    /// </remarks>
    public static byte BuildConfigByte(MuoviSignalMode mode) => mode switch
    {
        MuoviSignalMode.Emg        => 0x09,
        MuoviSignalMode.EmgLowGain => 0x0B,
        MuoviSignalMode.Eeg        => 0x01,
        _                          => 0x09
    };

    /// <summary>
    /// Computes an 8-bit CRC over <paramref name="data"/>[0..<paramref name="len"/>] (inclusive).
    /// Used by both the SyncStation and single-probe protocols to validate configuration packets.
    /// </summary>
    /// <param name="data">The byte array to checksum.</param>
    /// <param name="len">
    /// The index of the last byte to include (inclusive).
    /// For example, pass <c>pos - 1</c> to checksum bytes <c>0</c> through <c>pos - 1</c>.
    /// </param>
    /// <returns>The CRC-8 value as a single byte.</returns>
    public static byte Crc8(byte[] data, int len)
    {
        int crc = 0;
        for (int i = 0; i <= len; i++)
        {
            int ext = data[i];
            for (int j = 8; j > 0; j--)
            {
                int sum = (crc ^ ext) & 1;
                crc >>= 1;
                if (sum == 1) crc ^= 140;
                ext >>= 1;
            }
        }
        return (byte)crc;
    }

    /// <summary>
    /// Returns the human-readable channel label prefix for the specified signal mode.
    /// </summary>
    /// <param name="mode">The signal acquisition mode.</param>
    /// <returns><c>"EEG"</c> for <see cref="MuoviSignalMode.Eeg"/>, <c>"EMG"</c> otherwise.</returns>
    public static string ChannelPrefix(MuoviSignalMode mode) =>
        mode == MuoviSignalMode.Eeg ? "EEG" : "EMG";
}