using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.SignalProcessing;

namespace MOSAIC.Tests.Components.SignalProcessing;

/// <summary>
/// Invariant tests for the <see cref="FilterDesign"/> factory. The factory returns ready-to-use
/// <see cref="FirFilter"/>/<see cref="IirFilter"/> instances whose coefficient arrays are private,
/// so we probe the designs through their observable filtering behaviour rather than the raw taps.
///
/// FIR coefficient recovery: a FIR filter's response to a unit impulse IS its coefficient array.
/// Feeding x = [1, 0, 0, …, 0] of length N through y[n] = Σ_k b[k]·x[n−k] gives
/// y[k] = b[k] for k = 0 … N−1 (all other x terms are 0). So <see cref="FirImpulseResponse"/>
/// returns exactly the designed taps, letting us assert length, linear-phase symmetry, and DC gain
/// (the DC gain of a FIR filter is Σ b[k]).
///
/// IIR designs are verified via the exact <see cref="IirFilter.Order"/> and the exact DC gain
/// H(z=1) = Σb/Σa, which the transposed-DF2 difference equation reproduces as the steady-state
/// output of a constant (step) input once the transient has decayed.
/// </summary>
[TestClass]
public class FilterDesignTests
{
    // Recovers the FIR tap array by driving the filter with a unit impulse of length numTaps.
    private static double[] FirImpulseResponse(FirFilter filter, int numTaps)
    {
        var impulse = new double[numTaps];
        impulse[0] = 1.0;
        return filter.ProcessSamples(impulse);
    }

    // Feeds a constant (unit step) for many samples and returns the settled output = DC gain H(1).
    private static double SteadyStateGain(IOnlineFilter filter, int samples = 6000)
    {
        double last = 0.0;
        for (int i = 0; i < samples; i++)
            last = filter.ProcessSample(1.0);
        return last;
    }

    // ── FIR lowpass ─────────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(11)]
    [DataRow(21)]
    [DataRow(32)] // even tap count
    public void CreateFirLowpass_Order_EqualsNumTaps(int numTaps)
    {
        // A windowed-sinc FIR has exactly numTaps coefficients; FirFilter.Order returns that count.
        var filter = FilterDesign.CreateFirLowpass(sampleRate: 1000, cutoffFreq: 200, numTaps: numTaps);

        Assert.AreEqual(numTaps, filter.Order);
        Assert.AreEqual(numTaps, FirImpulseResponse(filter, numTaps).Length);
    }

    [TestMethod]
    public void CreateFirLowpass_Coefficients_AreSymmetric_LinearPhase()
    {
        // Windowed-sinc taps are even about the centre (sinc is even, the Hamming window is symmetric),
        // giving linear phase: b[i] == b[N-1-i] for every i.
        const int numTaps = 21;
        var filter = FilterDesign.CreateFirLowpass(sampleRate: 1000, cutoffFreq: 250, numTaps: numTaps);

        var b = FirImpulseResponse(filter, numTaps);

        Assert.AreEqual(numTaps, b.Length);
        for (int i = 0; i < numTaps / 2; i++)
            Assert.AreEqual(b[i], b[numTaps - 1 - i], 1e-12); // mirror taps must be identical
    }

    [TestMethod]
    public void CreateFirLowpass_HasUnityDcGain()
    {
        // The design normalises Σ b[k] = 1 (unity DC gain); the DC gain of a FIR filter is Σ b[k].
        const int numTaps = 41;
        var filter = FilterDesign.CreateFirLowpass(sampleRate: 2000, cutoffFreq: 300, numTaps: numTaps);

        var b = FirImpulseResponse(filter, numTaps);

        Assert.AreEqual(1.0, b.Sum(), 1e-9);
    }

    // ── FIR highpass ────────────────────────────────────────────────────

    [TestMethod]
    public void CreateFirHighpass_SumsToZero_AndIsSymmetric()
    {
        // Highpass = spectral inversion of a unity-DC lowpass: b_hp[i] = -b_lp[i], centre tap += 1.
        // DC gain = Σ b_hp = -Σ b_lp + 1 = -1 + 1 = 0. Odd numTaps keeps the +1 on the exact centre,
        // preserving symmetry b[i] == b[N-1-i].
        const int numTaps = 31; // odd => single centre tap
        var filter = FilterDesign.CreateFirHighpass(sampleRate: 1000, cutoffFreq: 150, numTaps: numTaps);

        var b = FirImpulseResponse(filter, numTaps);

        Assert.AreEqual(numTaps, b.Length);
        Assert.AreEqual(0.0, b.Sum(), 1e-9); // blocks DC
        for (int i = 0; i < numTaps / 2; i++)
            Assert.AreEqual(b[i], b[numTaps - 1 - i], 1e-12);
    }

    // ── FIR bandpass / bandstop ─────────────────────────────────────────

    [TestMethod]
    public void CreateFirBandpass_SumsToZero_BlocksDc()
    {
        // Bandpass = lowpass(highCut) − lowpass(lowCut); each lowpass sums to 1,
        // so Σ b = 1 − 1 = 0 (no DC passthrough). Taps stay symmetric (difference of symmetric arrays).
        const int numTaps = 31;
        var filter = FilterDesign.CreateFirBandpass(sampleRate: 1000, lowCutoff: 100, highCutoff: 300, numTaps: numTaps);

        var b = FirImpulseResponse(filter, numTaps);

        Assert.AreEqual(numTaps, b.Length);
        Assert.AreEqual(0.0, b.Sum(), 1e-9);
        for (int i = 0; i < numTaps / 2; i++)
            Assert.AreEqual(b[i], b[numTaps - 1 - i], 1e-12);
    }

    [TestMethod]
    public void CreateFirBandstop_SumsToUnity_PassesDc()
    {
        // Bandstop = spectral inversion of the bandpass: b_bs[i] = -b_bp[i], centre tap += 1.
        // Σ b_bs = -Σ b_bp + 1 = -0 + 1 = 1 (DC passes through). Odd numTaps => symmetric.
        const int numTaps = 31; // odd centre tap
        var filter = FilterDesign.CreateFirBandstop(sampleRate: 1000, lowCutoff: 100, highCutoff: 300, numTaps: numTaps);

        var b = FirImpulseResponse(filter, numTaps);

        Assert.AreEqual(numTaps, b.Length);
        Assert.AreEqual(1.0, b.Sum(), 1e-9); // passes DC
        for (int i = 0; i < numTaps / 2; i++)
            Assert.AreEqual(b[i], b[numTaps - 1 - i], 1e-12);
    }

    // ── IIR Butterworth lowpass ─────────────────────────────────────────

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    public void CreateIirLowpass_Order_EqualsRequestedOrder(int order)
    {
        // A Butterworth lowpass of order N yields denominator/numerator length N+1, so Order == N.
        var filter = FilterDesign.CreateIirLowpass(sampleRate: 1000, cutoffFreq: 200, order: order);

        Assert.AreEqual(order, filter.Order);
    }

    [TestMethod]
    public void CreateIirLowpass_PassesDc_UnityGain()
    {
        // A lowpass places its zeros at Nyquist (z=-1) and is normalised for unity gain at DC,
        // so a constant input settles to the same constant (H(z=1) = 1).
        var filter = FilterDesign.CreateIirLowpass(sampleRate: 1000, cutoffFreq: 200, order: 2);

        Assert.AreEqual(1.0, SteadyStateGain(filter), 1e-6);
    }

    // ── IIR Butterworth highpass ────────────────────────────────────────

    [TestMethod]
    public void CreateIirHighpass_OrderMatches_AndBlocksDc()
    {
        // A highpass places all its zeros at z=1, so the numerator vanishes at DC: H(z=1) = 0.
        // A constant input therefore decays to 0 in steady state.
        var filter = FilterDesign.CreateIirHighpass(sampleRate: 1000, cutoffFreq: 200, order: 2);

        Assert.AreEqual(2, filter.Order);
        Assert.AreEqual(0.0, SteadyStateGain(filter), 1e-6);
    }

    // ── IIR Butterworth bandpass / bandstop ─────────────────────────────

    [TestMethod]
    public void CreateIirBandpass_OrderIsDoubled_AndBlocksDc()
    {
        // The LP→BP transformation maps each of N prototype poles to 2, giving order 2N,
        // with N zeros at z=1 (DC) so a constant input decays to 0 (H(z=1) = 0).
        var filter = FilterDesign.CreateIirBandpass(sampleRate: 1000, lowCutoff: 150, highCutoff: 350, order: 2);

        Assert.AreEqual(4, filter.Order); // 2 × order
        Assert.AreEqual(0.0, SteadyStateGain(filter), 1e-6);
    }

    [TestMethod]
    public void CreateIirBandstop_OrderIsDoubled_AndPassesDc()
    {
        // The LP→BS transformation also gives order 2N; its zeros sit at the notch (±jω0),
        // and the design normalises for unity gain at DC, so a constant input settles to 1.
        var filter = FilterDesign.CreateIirBandstop(sampleRate: 1000, lowCutoff: 150, highCutoff: 350, order: 2);

        Assert.AreEqual(4, filter.Order); // 2 × order
        Assert.AreEqual(1.0, SteadyStateGain(filter), 1e-6);
    }
}
