namespace MOSAIC.Components.Devices.Quattrocento;

/// <summary>
/// Constants, lookup tables, and enumerations for the OT Bioelettronica Quattrocento amplifier.
/// </summary>
/// <remarks>
/// <para>
/// The Quattrocento supports up to 384 bioelectrical channels (8 × IN with 16 ch + 4 × MULTIPLE IN
/// with 64 ch), 16 auxiliary BNC inputs, and 8 internal accessory channels. The exact number of
/// active channels depends on the <see cref="ChannelSetting"/> selection.
/// </para>
/// <para>
/// All constants are derived from the OT Bioelettronica Configuration Protocol v1.7 and
/// Quattrocento User Manual v2.0 (November 2024).
/// </para>
/// </remarks>
public static class QuattrocentoConstants
{
    #region Channel Counts

    /// <summary>Number of accessory channels always appended to every sample frame.</summary>
    /// <remarks>
    /// Accessory channels: (1) sample counter, (2) trigger, (3) reserved,
    /// (4) buffer usage, (5–8) reserved.
    /// </remarks>
    public const int AccessoryChannelCount = 8;

    /// <summary>Number of auxiliary BNC inputs on the rear panel (always transferred).</summary>
    public const int AuxChannelCount = 16;

    /// <summary>
    /// Total channel counts (bio + aux + accessory) for each <see cref="ChannelSetting"/> value.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    ///   <listheader><term>Index</term><description>Active inputs → total channels</description></listheader>
    ///   <item><term>0</term><description>IN1–2, MULT1 → 96 bio + 16 aux + 8 acc = 120</description></item>
    ///   <item><term>1</term><description>IN1–4, MULT1–2 → 192 bio + 16 aux + 8 acc = 216</description></item>
    ///   <item><term>2</term><description>IN1–6, MULT1–3 → 288 bio + 16 aux + 8 acc = 312</description></item>
    ///   <item><term>3</term><description>IN1–8, MULT1–4 → 384 bio + 16 aux + 8 acc = 408</description></item>
    /// </list>
    /// </remarks>
    public static readonly int[] TotalChannelCounts = { 120, 216, 312, 408 };

    /// <summary>
    /// Returns the number of bioelectrical (EMG/EEG) channels for the given total channel count.
    /// </summary>
    public static int BioChannelCount(int totalChannels) => totalChannels - AuxChannelCount - AccessoryChannelCount;

    /// <summary>
    /// Returns the number of signal channels (bio + aux, excluding accessory) for the given total.
    /// This is the column count of the output matrix.
    /// </summary>
    public static int SignalChannelCount(int totalChannels) => totalChannels - AccessoryChannelCount;

    #endregion

    #region Sample Rates

    /// <summary>
    /// Sampling frequency values (Hz) for each <see cref="SamplingFrequency"/> selector.
    /// </summary>
    public static readonly int[] SampleRateValues = { 512, 2048, 5120, 10240 };

    #endregion

    #region Gain Factors

    /// <summary>
    /// Bio-channel gain factor: raw <c>int16</c> → millivolts.
    /// </summary>
    /// <remarks>
    /// Derivation: ADC range 5 V, 16-bit resolution (2^16 levels), hardware gain 150 V/V.
    /// <c>5 / 65536 / 150 × 1000 ≈ 5.086 × 10⁻⁴ mV/LSB</c>.
    /// </remarks>
    public const double BioGainFactor = 5.0 / 65536.0 / 150.0 * 1000.0;

    /// <summary>
    /// Auxiliary-channel gain factor: raw <c>int16</c> → volts.
    /// </summary>
    /// <remarks>
    /// Derivation: ADC range 5 V, 16-bit resolution, hardware gain 0.5 V/V.
    /// <c>5 / 65536 / 0.5 ≈ 1.526 × 10⁻⁴ V/LSB</c>.
    /// </remarks>
    public const double AuxGainFactor = 5.0 / 65536.0 / 0.5;

    #endregion

    #region Config Command

    /// <summary>Configuration command length in bytes (39 payload + 1 CRC-8).</summary>
    public const int ConfigCommandLength = 40;

    /// <summary>Number of configurable inputs (IN1–IN8 + MULTIPLE IN1–IN4).</summary>
    public const int InputCount = 12;

    /// <summary>Bytes per input configuration block (CONF0 + CONF1 + CONF2).</summary>
    public const int BytesPerInputConfig = 3;

    #endregion

    #region Accessory Channel Offsets

    /// <summary>
    /// Returns the device-frame channel index of the sample counter (accessory ch 1).
    /// </summary>
    public static int RampChannelIndex(int totalChannels) => totalChannels - AccessoryChannelCount;

    /// <summary>
    /// Returns the device-frame channel index of the trigger channel (accessory ch 2).
    /// </summary>
    public static int TriggerChannelIndex(int totalChannels) => totalChannels - AccessoryChannelCount + 1;

    /// <summary>
    /// Returns the device-frame channel index of the buffer usage indicator (accessory ch 4).
    /// </summary>
    public static int BufferUsageChannelIndex(int totalChannels) => totalChannels - AccessoryChannelCount + 3;

    #endregion
}

#region Enums

/// <summary>
/// Sampling frequency selector for the Quattrocento ACQ_SETT byte (bits 4–3).
/// </summary>
public enum SamplingFrequency
{
    /// <summary>512 Hz (FSAMP = 00).</summary>
    Hz512 = 0,

    /// <summary>2048 Hz (FSAMP = 01).</summary>
    Hz2048 = 1,

    /// <summary>5120 Hz (FSAMP = 10).</summary>
    Hz5120 = 2,

    /// <summary>10240 Hz (FSAMP = 11).</summary>
    Hz10240 = 3
}

/// <summary>
/// Channel count selector for the Quattrocento ACQ_SETT byte (bits 2–1).
/// </summary>
public enum ChannelSetting
{
    /// <summary>IN1–2, MULTIPLE IN1 → 120 total channels (NCH = 00).</summary>
    Ch120 = 0,

    /// <summary>IN1–4, MULTIPLE IN1–2 → 216 total channels (NCH = 01).</summary>
    Ch216 = 1,

    /// <summary>IN1–6, MULTIPLE IN1–3 → 312 total channels (NCH = 10).</summary>
    Ch312 = 2,

    /// <summary>IN1–8, MULTIPLE IN1–4 → 408 total channels (NCH = 11).</summary>
    Ch408 = 3
}

/// <summary>
/// Detection mode for a Quattrocento input (INx_CONF2 bits 1–0).
/// </summary>
public enum DetectionMode
{
    /// <summary>Monopolar: each channel vs. amplifier reference (MODE = 00).</summary>
    Monopolar = 0,

    /// <summary>Differential / single differential: adjacent electrode pairs (MODE = 01).</summary>
    Differential = 1,

    /// <summary>Bipolar: electrode pairs via AD8x2JD adapter (MODE = 10).</summary>
    Bipolar = 2
}

/// <summary>
/// High-pass filter cutoff for a Quattrocento input (INx_CONF2 bits 5–4).
/// </summary>
public enum HighPassFilter
{
    /// <summary>0.7 Hz (HPF = 00).</summary>
    Hz0_7 = 0,

    /// <summary>10 Hz (HPF = 01).</summary>
    Hz10 = 1,

    /// <summary>100 Hz (HPF = 10).</summary>
    Hz100 = 2,

    /// <summary>200 Hz (HPF = 11).</summary>
    Hz200 = 3
}

/// <summary>
/// Low-pass filter cutoff for a Quattrocento input (INx_CONF2 bits 3–2).
/// </summary>
public enum LowPassFilter
{
    /// <summary>130 Hz (LPF = 00).</summary>
    Hz130 = 0,

    /// <summary>500 Hz (LPF = 01).</summary>
    Hz500 = 1,

    /// <summary>900 Hz (LPF = 10).</summary>
    Hz900 = 2,

    /// <summary>4400 Hz (LPF = 11).</summary>
    Hz4400 = 3
}

/// <summary>
/// Body side annotation for a Quattrocento input (INx_CONF2 bits 7–6).
/// </summary>
public enum BodySide
{
    /// <summary>Not defined (SIDE = 00).</summary>
    NotDefined = 0,

    /// <summary>Left side (SIDE = 01).</summary>
    Left = 1,

    /// <summary>Right side (SIDE = 10).</summary>
    Right = 2,

    /// <summary>None / not applicable (SIDE = 11).</summary>
    None = 3
}

#endregion