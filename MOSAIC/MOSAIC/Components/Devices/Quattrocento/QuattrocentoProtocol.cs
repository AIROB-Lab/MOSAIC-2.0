using System;
using static MOSAIC.Components.Devices.Quattrocento.QuattrocentoConstants;

namespace MOSAIC.Components.Devices.Quattrocento;

/// <summary>
/// Configuration for a single Quattrocento input (IN1–IN8 or MULTIPLE IN1–IN4).
/// Maps to the three-byte CONF0/CONF1/CONF2 block in the 40-byte command.
/// </summary>
/// <remarks>
/// <para>
/// Each input occupies 3 consecutive bytes in the configuration command:
/// <list type="table">
///   <listheader><term>Byte</term><description>Content</description></listheader>
///   <item><term>CONF0</term><description>MUS&lt;6:0&gt; — muscle index (0–64)</description></item>
///   <item><term>CONF1</term><description>SENS&lt;4:0&gt; | ADAPT&lt;2:0&gt; — sensor and adapter type</description></item>
///   <item><term>CONF2</term><description>SIDE&lt;1:0&gt; | HPF&lt;1:0&gt; | LPF&lt;1:0&gt; | MODE&lt;1:0&gt;</description></item>
/// </list>
/// </para>
/// </remarks>
public readonly struct InputConfig
{
    /// <summary>Muscle index (0 = not defined, 1–64 = specific muscle). Default 0.</summary>
    public byte MuscleIndex { get; init; }

    /// <summary>Sensor type code (0 = not defined). See protocol doc SENS&lt;4:0&gt;.</summary>
    public byte SensorType { get; init; }

    /// <summary>Adapter type code (0 = not defined). See protocol doc ADAPT&lt;2:0&gt;.</summary>
    public byte AdapterType { get; init; }

    /// <summary>Body side annotation.</summary>
    public BodySide Side { get; init; }

    /// <summary>High-pass filter cutoff.</summary>
    public HighPassFilter HighPass { get; init; }

    /// <summary>Low-pass filter cutoff.</summary>
    public LowPassFilter LowPass { get; init; }

    /// <summary>Detection mode (monopolar / differential / bipolar).</summary>
    public DetectionMode Mode { get; init; }

    /// <summary>
    /// Default input configuration: 10 Hz HPF, 500 Hz LPF, monopolar, nothing else defined.
    /// </summary>
    public static readonly InputConfig Default = new()
    {
        MuscleIndex = 0,
        SensorType  = 0,
        AdapterType = 0,
        Side        = BodySide.NotDefined,
        HighPass    = HighPassFilter.Hz10,
        LowPass     = LowPassFilter.Hz500,
        Mode        = DetectionMode.Monopolar
    };

    /// <summary>
    /// Encodes this configuration into the three CONF bytes.
    /// </summary>
    /// <param name="conf0">CONF0: muscle index (bit 7 fixed 0).</param>
    /// <param name="conf1">CONF1: SENS&lt;4:0&gt; | ADAPT&lt;2:0&gt;.</param>
    /// <param name="conf2">CONF2: SIDE&lt;1:0&gt; | HPF&lt;1:0&gt; | LPF&lt;1:0&gt; | MODE&lt;1:0&gt;.</param>
    public void Encode(out byte conf0, out byte conf1, out byte conf2)
    {
        conf0 = (byte)(MuscleIndex & 0x7F);

        conf1 = (byte)(
              ((SensorType  & 0x1F) << 3)
            | ( AdapterType & 0x07)
        );

        conf2 = (byte)(
              (((int)Side     & 0x03) << 6)
            | (((int)HighPass & 0x03) << 4)
            | (((int)LowPass  & 0x03) << 2)
            | ( (int)Mode     & 0x03)
        );
    }
}

/// <summary>
/// Builds and validates the 40-byte configuration command for the Quattrocento amplifier,
/// including CRC-8 calculation.
/// </summary>
/// <remarks>
/// <para>
/// The command layout (per OT Bioelettronica Configuration Protocol v1.7):
/// <list type="table">
///   <listheader><term>Byte(s)</term><description>Content</description></listheader>
///   <item><term>0</term><description>ACQ_SETT: [1|DECIM|REC_ON|FSAMP1|FSAMP0|NCH1|NCH0|ACQ_ON]</description></item>
///   <item><term>1</term><description>AN_OUT_IN_SEL (analog output source + gain)</description></item>
///   <item><term>2</term><description>AN_OUT_CH_SEL (analog output channel)</description></item>
///   <item><term>3–38</term><description>12 × 3 bytes: per-input CONF0/1/2</description></item>
///   <item><term>39</term><description>CRC-8</description></item>
/// </list>
/// </para>
/// </remarks>
public static class QuattrocentoProtocol
{
    /// <summary>
    /// Builds the complete 40-byte configuration command.
    /// </summary>
    /// <param name="acqOn">Start (<see langword="true"/>) or stop (<see langword="false"/>) acquisition.</param>
    /// <param name="recOn">Set the REC_ON trigger bit for external synchronisation.</param>
    /// <param name="decimator">Enable the decimator for stable sampling.</param>
    /// <param name="fsamp">Sampling frequency selector (0–3).</param>
    /// <param name="nch">Channel count selector (0–3).</param>
    /// <param name="inputConfigs">
    /// Per-input configurations (12 entries for IN1–IN8 + MULT1–MULT4).
    /// If <see langword="null"/>, all inputs use <see cref="InputConfig.Default"/>.
    /// </param>
    /// <param name="anOutSource">Analog output source input (0–12). Default 0 (disabled).</param>
    /// <param name="anOutChannel">Analog output channel within the source (0–63). Default 0.</param>
    /// <param name="anOutGain">Analog output gain code (0=×1, 1=×2, 2=×4, 3=×16). Default 0.</param>
    /// <returns>40-byte command array with CRC-8 in byte 39.</returns>
    public static byte[] BuildCommand(
        bool          acqOn,
        bool          recOn,
        bool          decimator    = true,
        int           fsamp        = 3,
        int           nch          = 3,
        InputConfig[]? inputConfigs = null,
        int           anOutSource  = 0,
        int           anOutChannel = 0,
        int           anOutGain    = 0)
    {
        var cmd = new byte[ConfigCommandLength];

        // --- Byte 0: ACQ_SETT ---
        cmd[0] = (byte)(
              0x80                                       // bit 7 always 1
            | (decimator ? 0x40 : 0x00)                  // bit 6 DECIM
            | (recOn     ? 0x20 : 0x00)                  // bit 5 REC_ON
            | ((fsamp & 0x03) << 3)                      // bits 4-3 FSAMP<1:0>
            | ((nch   & 0x03) << 1)                      // bits 2-1 NCH<1:0>
            | (acqOn ? 0x01 : 0x00)                      // bit 0 ACQ_ON
        );

        // --- Byte 1: AN_OUT_IN_SEL ---
        cmd[1] = (byte)(
              ((anOutGain   & 0x03) << 4)
            | ( anOutSource & 0x0F)
        );

        // --- Byte 2: AN_OUT_CH_SEL ---
        cmd[2] = (byte)(anOutChannel & 0x3F);

        // --- Bytes 3–38: Per-input configuration (12 inputs × 3 bytes) ---
        for (int i = 0; i < InputCount; i++)
        {
            var cfg = (inputConfigs is not null && i < inputConfigs.Length)
                ? inputConfigs[i]
                : InputConfig.Default;

            cfg.Encode(out var c0, out var c1, out var c2);

            int baseIdx = 3 + i * BytesPerInputConfig;
            cmd[baseIdx + 0] = c0;
            cmd[baseIdx + 1] = c1;
            cmd[baseIdx + 2] = c2;
        }

        // --- Byte 39: CRC-8 ---
        cmd[39] = ComputeCrc8(cmd, 39);

        return cmd;
    }

    /// <summary>
    /// CRC-8 calculation matching the Quattrocento firmware.
    /// Uses reflected polynomial 0x8C (CRC-8/MAXIM variant).
    /// </summary>
    /// <param name="data">Input byte array.</param>
    /// <param name="length">Number of bytes to include in the calculation.</param>
    /// <returns>The computed CRC-8 check byte.</returns>
    public static byte ComputeCrc8(byte[] data, int length)
    {
        byte crc = 0;
        for (int j = 0; j < length; j++)
        {
            byte extract = data[j];
            for (int bit = 0; bit < 8; bit++)
            {
                int sum = (crc & 1) ^ (extract & 1);
                crc     = (byte)(crc >> 1);
                if (sum != 0)
                    crc ^= 0x8C; // reflected polynomial (140 decimal)
                extract = (byte)(extract >> 1);
            }
        }
        return crc;
    }
}