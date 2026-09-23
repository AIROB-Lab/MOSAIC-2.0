using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.FlowControl;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="ZeroCrossings"/>.
/// Per channel, count consecutive sample pairs (x[r], x[r+1]) that have strictly opposite
/// signs (one &gt; 0 and the other &lt; 0), provided |x[r] − x[r+1]| ≥ Threshold.
/// Rows = samples, columns = channels.
/// </summary>
[TestClass]
public class ZeroCrossingsTests
{
    [TestMethod]
    public void OnReceive_CleanAlternatingSignal_CountsEveryCrossing()
    {
        // Signal [1, -1, 1, -1]. Pairs: (1,-1) opposite, (-1,1) opposite, (1,-1) opposite.
        // Threshold 0 => every |diff| = 2 ≥ 0 counts. Crossings = 3.
        using var block = new ZeroCrossings(name: "ZC", threshold: 0.0, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0 },
            { -1.0 },
            { 1.0 },
            { -1.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(3.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_ThresholdOnDifferenceMagnitude_SuppressesTinyCrossing()
    {
        // Signal [0.4, -0.4, 5, -5], Threshold 1.0. Threshold gates |x[r] - x[r+1]|.
        // (0.4,-0.4): opposite signs, |0.8| = 0.8 < 1.0 => suppressed.
        // (-0.4, 5): opposite signs, |5.4| ≥ 1.0 => counted.
        // (5, -5):   opposite signs, |10|  ≥ 1.0 => counted.
        // Crossings = 2 (the small-amplitude crossing is filtered out).
        using var block = new ZeroCrossings(name: "ZC", threshold: 1.0, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 0.4 },
            { -0.4 },
            { 5.0 },
            { -5.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(2.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_ThresholdEqualToDifference_CountsBecauseComparisonIsInclusive()
    {
        // Signal [1, -1], Threshold 2.0. (1,-1) opposite signs, |1 - (-1)| = 2.
        // Comparison is |diff| >= Threshold, so 2 ≥ 2 counts. Crossings = 1.
        using var block = new ZeroCrossings(name: "ZC", threshold: 2.0, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0 },
            { -1.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(1.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_ConstantSignal_ProducesZero()
    {
        // Signal [7, 7, 7]. No pair has opposite signs => no crossings. ZC = 0.
        using var block = new ZeroCrossings(name: "ZC", threshold: 0.0, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 7.0 },
            { 7.0 },
            { 7.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(0.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_MultiChannelWindow_CountsEachChannelIndependently()
    {
        // Threshold 0 (every |diff| ≥ 0).
        // Ch0 [1, -1, 1]:  (1,-1) opposite, (-1,1) opposite => 2 crossings.
        // Ch1 [2,  2, -3]: (2,2) same sign (no crossing), (2,-3) opposite => 1 crossing.
        using var block = new ZeroCrossings(name: "ZC", threshold: 0.0, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0 },
            { -1.0, 2.0 },
            { 1.0, -3.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(2.0, result[0], 1e-9);
        Assert.AreEqual(1.0, result[1], 1e-9);
    }

    [TestMethod]
    public void OnReceive_ZeroValuedSample_IsNeitherPositiveNorNegative_NotACrossing()
    {
        // Signal [1, 0, -1]. The comparison requires strictly (>0 and <0).
        // (1, 0):  0 is not < 0 => no crossing.
        // (0, -1): 0 is not > 0 => no crossing.
        // ZC = 0 (a sample sitting exactly on zero does not create a counted crossing).
        using var block = new ZeroCrossings(name: "ZC", threshold: 0.0, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0 },
            { 0.0 },
            { -1.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(0.0, result[0], 1e-9);
    }
}
