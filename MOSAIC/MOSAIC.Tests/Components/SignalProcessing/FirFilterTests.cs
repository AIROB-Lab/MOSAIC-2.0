using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.SignalProcessing;

namespace MOSAIC.Tests.Components.SignalProcessing;

/// <summary>
/// Known-answer tests for <see cref="FirFilter"/>.
///
/// The filter convolves the input with its coefficient array using a circular delay line:
///   y[n] = Σ_{k=0..N-1} b[k] · x[n−k],
/// where b[0] multiplies the newest sample x[n] and b[k] multiplies the k-samples-old input
/// (unavailable history reads as 0 before the buffer fills). Expected values below are traced
/// by hand from that definition, NOT paraphrased from the C# loop.
/// </summary>
[TestClass]
public class FirFilterTests
{
    [TestMethod]
    public void Order_ReturnsCoefficientCount()
    {
        // Order is defined as the number of taps (coefficients).
        var filter = new FirFilter(new[] { 0.1, 0.2, 0.3, 0.4 });

        Assert.AreEqual(4, filter.Order);
    }

    [TestMethod]
    public void Constructor_NullCoefficients_Throws()
    {
        // Coefficients are required; a null array is rejected up front.
        Assert.ThrowsExactly<ArgumentNullException>(() => new FirFilter(null!));
    }

    [TestMethod]
    public void ProcessSamples_TwoTapMovingSum_MatchesHandTrace()
    {
        // Coeffs [0.5, 0.5], inputs 1,2,3.  b[0]·x[n] + b[1]·x[n-1] (x[-1]=0):
        //   y[0] = 0.5·1 + 0.5·0   = 0.5
        //   y[1] = 0.5·2 + 0.5·1   = 1.5
        //   y[2] = 0.5·3 + 0.5·2   = 2.5
        var filter = new FirFilter(new[] { 0.5, 0.5 });

        var output = filter.ProcessSamples(new[] { 1.0, 2.0, 3.0 });

        Assert.AreEqual(3, output.Length);
        Assert.AreEqual(0.5, output[0], 1e-6);
        Assert.AreEqual(1.5, output[1], 1e-6);
        Assert.AreEqual(2.5, output[2], 1e-6);
    }

    [TestMethod]
    public void ProcessSamples_ThreeTapWeightedSum_MatchesHandTrace()
    {
        // Coeffs [0.25, 0.5, 0.25], inputs 10,20,30.  y[n] = b0·x[n] + b1·x[n-1] + b2·x[n-2]:
        //   y[0] = 0.25·10 + 0.5·0  + 0.25·0  = 2.5
        //   y[1] = 0.25·20 + 0.5·10 + 0.25·0  = 5 + 5      = 10.0
        //   y[2] = 0.25·30 + 0.5·20 + 0.25·10 = 7.5 + 10 + 2.5 = 20.0
        var filter = new FirFilter(new[] { 0.25, 0.5, 0.25 });

        var output = filter.ProcessSamples(new[] { 10.0, 20.0, 30.0 });

        Assert.AreEqual(3, output.Length);
        Assert.AreEqual(2.5, output[0], 1e-6);
        Assert.AreEqual(10.0, output[1], 1e-6);
        Assert.AreEqual(20.0, output[2], 1e-6);
    }

    [TestMethod]
    public void ProcessSample_SingleTapGain_ScalesEachSample()
    {
        // A one-tap filter [2.0] is a pure gain: y[n] = 2·x[n], no history involved.
        var filter = new FirFilter(new[] { 2.0 });

        Assert.AreEqual(6.0, filter.ProcessSample(3.0), 1e-6);   // 2·3
        Assert.AreEqual(-8.0, filter.ProcessSample(-4.0), 1e-6); // 2·-4
        Assert.AreEqual(0.0, filter.ProcessSample(0.0), 1e-6);   // 2·0
    }

    [TestMethod]
    public void Reset_ClearsDelayLine_SoHistoryDoesNotLeak()
    {
        // Coeffs [0.5, 0.5]. Prime the delay line with 1,2 (leaves x[n-1]=2 in state).
        // After Reset the history is zeroed, so feeding 3 again must behave as a fresh start:
        //   y = 0.5·3 + 0.5·0 = 1.5   (would be 0.5·3 + 0.5·2 = 2.5 without the reset).
        var filter = new FirFilter(new[] { 0.5, 0.5 });
        filter.ProcessSample(1.0);
        filter.ProcessSample(2.0);

        filter.Reset();
        double afterReset = filter.ProcessSample(3.0);

        Assert.AreEqual(1.5, afterReset, 1e-6);
    }

    [TestMethod]
    public void ProcessSamples_EmptyInput_ReturnsEmptyArray()
    {
        // No samples in => empty (but non-null) output; the filter must not throw on empty input.
        var filter = new FirFilter(new[] { 0.5, 0.5 });

        var output = filter.ProcessSamples(Array.Empty<double>());

        Assert.AreEqual(0, output.Length);
    }

    [TestMethod]
    public void ProcessSamples_CircularBufferWraps_MatchesHandTrace()
    {
        // Coeffs [1,0,0] with a 3-tap buffer forces the delay index to wrap after 3 samples.
        // Since only b[0] is non-zero, y[n] = 1·x[n]; the output must echo the input exactly
        // across the wrap boundary (samples 4 and 5), proving the circular indexing is correct.
        var filter = new FirFilter(new[] { 1.0, 0.0, 0.0 });

        var output = filter.ProcessSamples(new[] { 5.0, 6.0, 7.0, 8.0, 9.0 });

        Assert.AreEqual(5, output.Length);
        Assert.AreEqual(5.0, output[0], 1e-9);
        Assert.AreEqual(6.0, output[1], 1e-9);
        Assert.AreEqual(7.0, output[2], 1e-9);
        Assert.AreEqual(8.0, output[3], 1e-9); // buffer wrapped here: index returned to 0
        Assert.AreEqual(9.0, output[4], 1e-9);
    }
}
