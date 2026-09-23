using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.SignalProcessing;

namespace MOSAIC.Tests.Components.SignalProcessing;

/// <summary>
/// Known-answer tests for <see cref="IirFilter"/>, a Direct Form II Transposed IIR filter.
///
/// Difference equation:
///   y[n] = b[0]·x[n] + b[1]·x[n−1] + …  −  (a[1]·y[n−1] + a[2]·y[n−2] + …)
/// with feedforward coefficients b (numerator) and feedback coefficients a (denominator).
/// If a[0] ≠ 1, both arrays are normalised by a[0] at construction.
///
/// Every expected value below is derived directly from that difference equation, iterating
/// n forward from rest (all past x and y equal 0 before the first sample).
/// </summary>
[TestClass]
public class IirFilterTests
{
    [TestMethod]
    public void ProcessSamples_Identity_PassesInputThroughUnchanged()
    {
        // b=[1], a=[1]  =>  y[n] = 1·x[n]. Pure pass-through for every sample.
        var filter = new IirFilter(new[] { 1.0 }, new[] { 1.0 });
        var input = new[] { 3.0, -2.0, 5.0, 7.0 };

        var output = filter.ProcessSamples(input);

        Assert.AreEqual(input.Length, output.Length);
        Assert.AreEqual(3.0, output[0], 1e-9);   // y[0] = x[0] = 3
        Assert.AreEqual(-2.0, output[1], 1e-9);  // y[1] = x[1] = -2
        Assert.AreEqual(5.0, output[2], 1e-9);   // y[2] = x[2] = 5
        Assert.AreEqual(7.0, output[3], 1e-9);   // y[3] = x[3] = 7
    }

    [TestMethod]
    public void ProcessSamples_PureGain_ScalesEachSample()
    {
        // b=[0.1], a=[1]  =>  y[n] = 0.1·x[n]. Memoryless scaling by 0.1.
        var filter = new IirFilter(new[] { 0.1 }, new[] { 1.0 });
        var input = new[] { 1.0, 10.0, -5.0 };

        var output = filter.ProcessSamples(input);

        Assert.AreEqual(input.Length, output.Length);
        Assert.AreEqual(0.1, output[0], 1e-9);   // 0.1·1   = 0.1
        Assert.AreEqual(1.0, output[1], 1e-9);   // 0.1·10  = 1.0
        Assert.AreEqual(-0.5, output[2], 1e-9);  // 0.1·-5  = -0.5
    }

    [TestMethod]
    public void ProcessSamples_TwoTapFir_MixesCurrentAndPreviousSample()
    {
        // b=[0.5,0.5], a=[1]  =>  y[n] = 0.5·x[n] + 0.5·x[n−1]  (FIR, x[-1]=0).
        var filter = new IirFilter(new[] { 0.5, 0.5 }, new[] { 1.0 });
        var input = new[] { 2.0, 4.0, 6.0, 0.0 };

        var output = filter.ProcessSamples(input);

        Assert.AreEqual(input.Length, output.Length);
        Assert.AreEqual(1.0, output[0], 1e-9);  // 0.5·2 + 0.5·0 = 1
        Assert.AreEqual(3.0, output[1], 1e-9);  // 0.5·4 + 0.5·2 = 3
        Assert.AreEqual(5.0, output[2], 1e-9);  // 0.5·6 + 0.5·4 = 5
        Assert.AreEqual(3.0, output[3], 1e-9);  // 0.5·0 + 0.5·6 = 3
    }

    [TestMethod]
    public void ProcessSamples_LeakyIntegratorImpulse_DecaysByHalfEachStep()
    {
        // b=[1], a=[1,-0.5]  =>  y[n] = x[n] + 0.5·y[n−1]. Impulse x=[1,0,0,0]:
        // y0=1; y1=0.5·1=0.5; y2=0.5·0.5=0.25; y3=0.5·0.25=0.125 (geometric decay).
        var filter = new IirFilter(new[] { 1.0 }, new[] { 1.0, -0.5 });
        var input = new[] { 1.0, 0.0, 0.0, 0.0 };

        var output = filter.ProcessSamples(input);

        Assert.AreEqual(input.Length, output.Length);
        Assert.AreEqual(1.0, output[0], 1e-6);
        Assert.AreEqual(0.5, output[1], 1e-6);
        Assert.AreEqual(0.25, output[2], 1e-6);
        Assert.AreEqual(0.125, output[3], 1e-6);
    }

    [TestMethod]
    public void ProcessSample_LeakyIntegratorStep_AccumulatesTowardTwo()
    {
        // b=[1], a=[1,-0.5]  =>  y[n] = x[n] + 0.5·y[n−1]. Unit step x=1 each call:
        // y0=1; y1=1+0.5·1=1.5; y2=1+0.5·1.5=1.75; y3=1+0.5·1.75=1.875; y4=1+0.5·1.875=1.9375.
        var filter = new IirFilter(new[] { 1.0 }, new[] { 1.0, -0.5 });

        Assert.AreEqual(1.0, filter.ProcessSample(1.0), 1e-6);
        Assert.AreEqual(1.5, filter.ProcessSample(1.0), 1e-6);
        Assert.AreEqual(1.75, filter.ProcessSample(1.0), 1e-6);
        Assert.AreEqual(1.875, filter.ProcessSample(1.0), 1e-6);
        Assert.AreEqual(1.9375, filter.ProcessSample(1.0), 1e-6);
    }

    [TestMethod]
    public void ProcessSamples_GeneralFirstOrder_MatchesDifferenceEquation()
    {
        // b=[0.2,0.3], a=[1,-0.4]  =>  y[n] = 0.2·x[n] + 0.3·x[n−1] + 0.4·y[n−1].
        // x=[1,2,3]:
        //   y0 = 0.2·1 + 0.3·0 + 0.4·0            = 0.2
        //   y1 = 0.2·2 + 0.3·1 + 0.4·0.2 = 0.4+0.3+0.08   = 0.78
        //   y2 = 0.2·3 + 0.3·2 + 0.4·0.78 = 0.6+0.6+0.312 = 1.512
        var filter = new IirFilter(new[] { 0.2, 0.3 }, new[] { 1.0, -0.4 });
        var input = new[] { 1.0, 2.0, 3.0 };

        var output = filter.ProcessSamples(input);

        Assert.AreEqual(input.Length, output.Length);
        Assert.AreEqual(0.2, output[0], 1e-6);
        Assert.AreEqual(0.78, output[1], 1e-6);
        Assert.AreEqual(1.512, output[2], 1e-6);
    }

    [TestMethod]
    public void Constructor_NonUnityA0_NormalisesCoefficients()
    {
        // b=[2], a=[2,-1] is normalised by a[0]=2  =>  b=[1], a=[1,-0.5],
        // i.e. the same leaky integrator y[n] = x[n] + 0.5·y[n−1]. Unit step:
        // y0=1; y1=1.5; y2=1.75; y3=1.875.
        var filter = new IirFilter(new[] { 2.0, }, new[] { 2.0, -1.0 });

        Assert.AreEqual(1.0, filter.ProcessSample(1.0), 1e-6);
        Assert.AreEqual(1.5, filter.ProcessSample(1.0), 1e-6);
        Assert.AreEqual(1.75, filter.ProcessSample(1.0), 1e-6);
        Assert.AreEqual(1.875, filter.ProcessSample(1.0), 1e-6);
    }

    [TestMethod]
    public void Reset_AfterProcessing_RestoresImpulseResponseFromRest()
    {
        // Feed the leaky integrator some samples so state (delay elements) is non-zero,
        // then Reset should clear all state. A fresh impulse must then yield y0 = 1
        // exactly (x[n] + 0.5·y[n−1] with y[-1] = 0), identical to first-run behaviour.
        var filter = new IirFilter(new[] { 1.0 }, new[] { 1.0, -0.5 });
        filter.ProcessSample(1.0);
        filter.ProcessSample(1.0);

        filter.Reset();
        var afterReset = filter.ProcessSample(1.0);

        Assert.AreEqual(1.0, afterReset, 1e-6);
    }

    [TestMethod]
    public void Order_IsMaxCoefficientLengthMinusOne()
    {
        // Order = max(b.Length, a.Length) − 1.
        var order0 = new IirFilter(new[] { 1.0 }, new[] { 1.0 });                 // max(1,1)-1 = 0
        var order1FromB = new IirFilter(new[] { 0.5, 0.5 }, new[] { 1.0 });       // max(2,1)-1 = 1
        var order2FromA = new IirFilter(new[] { 1.0 }, new[] { 1.0, -0.4, 0.1 }); // max(1,3)-1 = 2

        Assert.AreEqual(0, order0.Order);
        Assert.AreEqual(1, order1FromB.Order);
        Assert.AreEqual(2, order2FromA.Order);
    }

    [TestMethod]
    public void Constructor_NullCoefficients_Throws()
    {
        // Both coefficient arrays are required; null must be rejected up front.
        Assert.ThrowsExactly<ArgumentNullException>(() => new IirFilter(null!, new[] { 1.0 }));
        Assert.ThrowsExactly<ArgumentNullException>(() => new IirFilter(new[] { 1.0 }, null!));
    }
}
