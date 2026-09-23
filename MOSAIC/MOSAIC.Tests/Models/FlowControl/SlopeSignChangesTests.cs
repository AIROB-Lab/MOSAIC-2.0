using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.FlowControl;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="SlopeSignChanges"/>.
/// SSC per channel = count of interior samples r in [1, N-2] where the slope flips sign, i.e.
/// with diff1 = x[r] - x[r-1] and diff2 = x[r+1] - x[r], the sample counts when
/// (diff1 * diff2 &lt; 0) AND |diff1| &gt;= Threshold AND |diff2| &gt;= Threshold
/// (rows = samples, columns = channels).
/// </summary>
[TestClass]
public class SlopeSignChangesTests
{
    [TestMethod]
    public void OnReceive_AlternatingUpDownSignal_CountsEveryLocalExtremum()
    {
        // Signal [0, 2, 0, 2, 0], threshold = 0. Interior samples r = 1,2,3:
        // r=1: diff1=2-0=2,  diff2=0-2=-2 -> 2*-2=-4<0            -> peak,   count
        // r=2: diff1=0-2=-2, diff2=2-0=2  -> -2*2=-4<0            -> valley, count
        // r=3: diff1=2-0=2,  diff2=0-2=-2 -> 2*-2=-4<0            -> peak,   count
        // SSC = 3
        using var block = new SlopeSignChanges(name: "SSC", threshold: 0.0, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 0.0 },
            { 2.0 },
            { 0.0 },
            { 2.0 },
            { 0.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(3.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_ThresholdSuppressesSmallSignChanges()
    {
        // Signal [0, 0.5, 0, 5, 0], threshold = 1.0. Interior samples r = 1,2,3:
        // r=1: diff1=0.5,  diff2=-0.5 -> product<0 but |0.5|>=1 is FALSE   -> suppressed
        // r=2: diff1=-0.5, diff2=5    -> product<0 but |-0.5|>=1 is FALSE  -> suppressed
        // r=3: diff1=5,    diff2=-5   -> product<0, |5|>=1 and |-5|>=1     -> count
        // SSC = 1
        using var block = new SlopeSignChanges(name: "SSC", threshold: 1.0, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 0.0 },
            { 0.5 },
            { 0.0 },
            { 5.0 },
            { 0.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(1.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_ZeroThresholdCountsEverySignChangeTheThresholdWouldSuppress()
    {
        // Same signal [0, 0.5, 0, 5, 0] as the suppression test but threshold = 0.
        // r=1: diff1=0.5,  diff2=-0.5 -> product<0, magnitude gate open -> peak,   count
        // r=2: diff1=-0.5, diff2=5    -> product<0, magnitude gate open -> valley, count
        // r=3: diff1=5,    diff2=-5   -> product<0, magnitude gate open -> peak,   count
        // SSC = 3 (contrast with SSC = 1 when threshold = 1.0)
        using var block = new SlopeSignChanges(name: "SSC", threshold: 0.0, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 0.0 },
            { 0.5 },
            { 0.0 },
            { 5.0 },
            { 0.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(3.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_MonotonicSignal_ProducesZero()
    {
        // Signal [1, 2, 3, 4], threshold = 0. Interior samples r = 1,2:
        // r=1: diff1=2-1=1, diff2=3-2=1 -> product=1>0 (no sign change) -> not counted
        // r=2: diff1=3-2=1, diff2=4-3=1 -> product=1>0 (no sign change) -> not counted
        // SSC = 0
        using var block = new SlopeSignChanges(name: "SSC", threshold: 0.0, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0 },
            { 2.0 },
            { 3.0 },
            { 4.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(0.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_MultiChannelWindow_ComputesPerChannelIndependently()
    {
        // Ch0 = [0, 2, 0, 2, 0] (alternating)  -> 3 local extrema  -> SSC = 3
        // Ch1 = [1, 2, 3, 4, 5] (monotonic)     -> no sign changes -> SSC = 0
        // threshold = 0.
        using var block = new SlopeSignChanges(name: "SSC", threshold: 0.0, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 0.0, 1.0 },
            { 2.0, 2.0 },
            { 0.0, 3.0 },
            { 2.0, 4.0 },
            { 0.0, 5.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(3.0, result[0], 1e-9);
        Assert.AreEqual(0.0, result[1], 1e-9);
    }

    [TestMethod]
    public void OnReceive_DifferenceExactlyEqualsThreshold_IsCounted()
    {
        // Signal [0, 1, 0], threshold = 1.0. Only interior sample is r = 1:
        // r=1: diff1=1-0=1, diff2=0-1=-1 -> product=-1<0, |1|>=1 (equal) and |-1|>=1 (equal)
        // The gate uses >= (not >), so the boundary case IS counted -> SSC = 1.
        using var block = new SlopeSignChanges(name: "SSC", threshold: 1.0, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 0.0 },
            { 1.0 },
            { 0.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(1.0, result[0], 1e-9);
    }
}
