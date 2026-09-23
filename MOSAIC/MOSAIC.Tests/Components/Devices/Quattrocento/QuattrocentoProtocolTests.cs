using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Devices.Quattrocento;

namespace MOSAIC.Tests.Components.Devices.Quattrocento;

/// <summary>
/// Known-answer tests for <see cref="QuattrocentoProtocol"/>: the 40-byte configuration-command
/// builder and its CRC-8 trailer.
///
/// Command layout (OT Bioelettronica Configuration Protocol v1.7):
/// <list type="bullet">
///   <item><description>Byte 0 ACQ_SETT = 0x80 | DECIM&lt;&lt;6 | REC_ON&lt;&lt;5 | (FSAMP&amp;3)&lt;&lt;3 | (NCH&amp;3)&lt;&lt;1 | ACQ_ON.</description></item>
///   <item><description>Byte 1 AN_OUT_IN_SEL = (gain&amp;3)&lt;&lt;4 | (source&amp;0x0F).</description></item>
///   <item><description>Byte 2 AN_OUT_CH_SEL = channel &amp; 0x3F.</description></item>
///   <item><description>Bytes 3–38 = 12 inputs × (CONF0/CONF1/CONF2).</description></item>
///   <item><description>Byte 39 = CRC-8 over bytes 0–38 (reflected poly 0x8C, init 0 — Dallas/Maxim).</description></item>
/// </list>
///
/// Every expected value below is derived by hand from those definitions and noted on each assertion.
/// Byte values are compared as <c>int</c> so boxed byte/int type mismatches never mask a failure.
/// </summary>
[TestClass]
public class QuattrocentoProtocolTests
{
    // ---------------------------------------------------------------- BuildCommand: framing

    [TestMethod]
    public void BuildCommand_Always_ReturnsFortyByteArray()
    {
        // ConfigCommandLength = 39 payload bytes + 1 CRC-8 trailer = 40.
        var cmd = QuattrocentoProtocol.BuildCommand(acqOn: true, recOn: false);

        Assert.IsNotNull(cmd);
        Assert.AreEqual(40, cmd.Length);
    }

    // ---------------------------------------------------------------- Byte 0: ACQ_SETT

    [DataTestMethod]
    // 0x80(bit7) | 0x40(decim) | 0x18(fsamp3<<3) | 0x06(nch3<<1) | 0x01(acq) = 0xDF
    [DataRow(true, false, true, 3, 3, 0xDF)]
    // 0x80(bit7) | 0x20(recOn) only = 0xA0
    [DataRow(false, true, false, 0, 0, 0xA0)]
    // 0x80 | 0x40 | 0x20(rec) | 0x08(fsamp1<<3) | 0x04(nch2<<1) | 0x01 = 0xED
    [DataRow(true, true, true, 1, 2, 0xED)]
    // fsamp/nch masked to 2 bits: 7&3 = 3, so identical to the fsamp=3,nch=3 row => 0xDF
    [DataRow(true, false, true, 7, 7, 0xDF)]
    public void BuildCommand_AcqSettByte_PacksFlagsAndSelectors(
        bool acqOn, bool recOn, bool decimator, int fsamp, int nch, int expected)
    {
        var cmd = QuattrocentoProtocol.BuildCommand(
            acqOn: acqOn, recOn: recOn, decimator: decimator, fsamp: fsamp, nch: nch);

        Assert.AreEqual(expected, (int)cmd[0]);
    }

    // ---------------------------------------------------------------- Bytes 1–2: analog output

    [TestMethod]
    public void BuildCommand_AnalogOutputBytes_PackGainSourceAndChannel()
    {
        // Byte1 = (gain&3)<<4 | (source&0x0F) = (2<<4) | 5 = 0x20 | 0x05 = 0x25 = 37.
        // Byte2 = channel & 0x3F = 63 & 0x3F = 63.
        var cmd = QuattrocentoProtocol.BuildCommand(
            acqOn: false, recOn: false, anOutSource: 5, anOutChannel: 63, anOutGain: 2);

        Assert.AreEqual(37, (int)cmd[1]);
        Assert.AreEqual(63, (int)cmd[2]);
    }

    [TestMethod]
    public void BuildCommand_AnalogOutputChannel_MaskedToSixBits()
    {
        // channel 64 = 0b100_0000: bit 6 is dropped by & 0x3F => 0.
        var cmd = QuattrocentoProtocol.BuildCommand(
            acqOn: false, recOn: false, anOutChannel: 64);

        Assert.AreEqual(0, (int)cmd[2]);
    }

    [TestMethod]
    public void BuildCommand_AnalogOutputDefaults_ProduceZeroBytes()
    {
        // Default anOutGain=0, anOutSource=0, anOutChannel=0 => both bytes 0.
        var cmd = QuattrocentoProtocol.BuildCommand(acqOn: true, recOn: false);

        Assert.AreEqual(0, (int)cmd[1]);
        Assert.AreEqual(0, (int)cmd[2]);
    }

    // ---------------------------------------------------------------- Bytes 3–38: input configs

    [TestMethod]
    public void BuildCommand_NullInputConfigs_FillsAllTwelveInputsWithDefault()
    {
        // InputConfig.Default => CONF0=0, CONF1=0, CONF2 = (Hz10=1<<4)|(Hz500=1<<2) = 0x10|0x04 = 20.
        // 12 inputs occupy bytes 3..38 as the repeating triple [0, 0, 20].
        var cmd = QuattrocentoProtocol.BuildCommand(acqOn: true, recOn: false, inputConfigs: null);

        for (int i = 0; i < 12; i++)
        {
            int baseIdx = 3 + i * 3;
            Assert.AreEqual(0, (int)cmd[baseIdx + 0], $"CONF0 of input {i}");
            Assert.AreEqual(0, (int)cmd[baseIdx + 1], $"CONF1 of input {i}");
            Assert.AreEqual(20, (int)cmd[baseIdx + 2], $"CONF2 of input {i}");
        }
    }

    [TestMethod]
    public void BuildCommand_CustomInputConfig_EncodedIntoFirstConfBlock()
    {
        // Musc=5      => CONF0 = 5 & 0x7F = 5.
        // Sens=3,Adapt=2 => CONF1 = (3<<3)|2 = 24|2 = 26.
        // Side=Right(2),HPF=Hz100(2),LPF=Hz900(2),Mode=Bipolar(2)
        //             => CONF2 = (2<<6)|(2<<4)|(2<<2)|2 = 128|32|8|2 = 170.
        var custom = new InputConfig
        {
            MuscleIndex = 5,
            SensorType  = 3,
            AdapterType = 2,
            Side        = BodySide.Right,
            HighPass    = HighPassFilter.Hz100,
            LowPass     = LowPassFilter.Hz900,
            Mode        = DetectionMode.Bipolar
        };

        var cmd = QuattrocentoProtocol.BuildCommand(
            acqOn: true, recOn: false, inputConfigs: new[] { custom });

        Assert.AreEqual(5, (int)cmd[3]);   // CONF0 of input 0
        Assert.AreEqual(26, (int)cmd[4]);  // CONF1 of input 0
        Assert.AreEqual(170, (int)cmd[5]); // CONF2 of input 0
    }

    [TestMethod]
    public void BuildCommand_FewerConfigsThanInputs_RemainingInputsUseDefault()
    {
        // Only input 0 supplied; inputs 1..11 must fall back to InputConfig.Default ([0,0,20]).
        var custom = new InputConfig
        {
            MuscleIndex = 5,
            SensorType  = 3,
            AdapterType = 2,
            Side        = BodySide.Right,
            HighPass    = HighPassFilter.Hz100,
            LowPass     = LowPassFilter.Hz900,
            Mode        = DetectionMode.Bipolar
        };

        var cmd = QuattrocentoProtocol.BuildCommand(
            acqOn: true, recOn: false, inputConfigs: new[] { custom });

        // input 0 = custom
        Assert.AreEqual(5, (int)cmd[3]);
        Assert.AreEqual(26, (int)cmd[4]);
        Assert.AreEqual(170, (int)cmd[5]);

        // input 1 (bytes 6,7,8) = default
        Assert.AreEqual(0, (int)cmd[6]);
        Assert.AreEqual(0, (int)cmd[7]);
        Assert.AreEqual(20, (int)cmd[8]);

        // last input, index 11 (bytes 36,37,38) = default
        Assert.AreEqual(0, (int)cmd[36]);
        Assert.AreEqual(0, (int)cmd[37]);
        Assert.AreEqual(20, (int)cmd[38]);
    }

    // ---------------------------------------------------------------- Byte 39: CRC trailer

    [TestMethod]
    public void BuildCommand_TrailerByte_IsCrc8OverThePayload()
    {
        // Invariant: byte 39 is the CRC-8 of the preceding 39 payload bytes (indices 0..38).
        // ComputeCrc8 itself is independently hand-verified in the tests below.
        var cmd = QuattrocentoProtocol.BuildCommand(acqOn: true, recOn: false);

        byte expectedCrc = QuattrocentoProtocol.ComputeCrc8(cmd, 39);

        Assert.AreEqual((int)expectedCrc, (int)cmd[39]);
    }

    // ---------------------------------------------------------------- ComputeCrc8

    [TestMethod]
    public void ComputeCrc8_ZeroLength_ReturnsZero()
    {
        // The outer loop never runs => crc stays at its init value 0.
        byte crc = QuattrocentoProtocol.ComputeCrc8(new byte[] { 0x01, 0x02, 0x03 }, 0);

        Assert.AreEqual(0, (int)crc);
    }

    [TestMethod]
    public void ComputeCrc8_SingleZeroByte_ReturnsZero()
    {
        // Feeding 0x00 with crc=0: every bit's sum = 0^0 = 0, no polynomial xor, crc stays 0.
        byte crc = QuattrocentoProtocol.ComputeCrc8(new byte[] { 0x00 }, 1);

        Assert.AreEqual(0, (int)crc);
    }

    [DataTestMethod]
    // Hand-traced through the reflected (poly 0x8C, init 0) bit loop; these equal the classic
    // Dallas/Maxim CRC-8 table entries T[1]=94 and T[2]=188.
    [DataRow(0x01, 94)]
    [DataRow(0x02, 188)]
    public void ComputeCrc8_SingleByte_MatchesDallasMaximTable(int inputByte, int expected)
    {
        byte crc = QuattrocentoProtocol.ComputeCrc8(new byte[] { (byte)inputByte }, 1);

        Assert.AreEqual(expected, (int)crc);
    }

    [TestMethod]
    public void ComputeCrc8_TwoBytes_ReturnsHandDerivedValue()
    {
        // Sequential: after 0x01 crc=0x5E(94); feeding 0x02 from crc=0x5E yields 0x78(120).
        // Cross-checked via the table recurrence crc = T[crc ^ byte] = T[0x5E ^ 0x02] = T[0x5C] = 0x78.
        byte crc = QuattrocentoProtocol.ComputeCrc8(new byte[] { 0x01, 0x02 }, 2);

        Assert.AreEqual(120, (int)crc);
    }

    [TestMethod]
    public void ComputeCrc8_LengthParameter_TruncatesInput()
    {
        // Only the first byte is included, so trailing 0xFF/0xAA are ignored:
        // result must equal CRC-8 of [0x01] alone = 94.
        byte crc = QuattrocentoProtocol.ComputeCrc8(new byte[] { 0x01, 0xFF, 0xAA }, 1);

        Assert.AreEqual(94, (int)crc);
    }
}
