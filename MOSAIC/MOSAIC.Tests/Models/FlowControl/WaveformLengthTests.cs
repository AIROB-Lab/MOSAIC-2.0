using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.FlowControl;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="WaveformLength"/>.
/// WL per channel = Σ |x[r] − x[r−1]| for r = 1..N−1 (rows = samples, columns = channels).
/// </summary>
[TestClass]
public class WaveformLengthTests
{
    [TestMethod]
    public void OnReceive_SingleChannelWindow_PublishesSumOfAbsoluteDifferences()
    {
        // |3-1| + |2-3| + |5-2| = 2 + 1 + 3 = 6
        using var block = new WaveformLength(name: "WL", desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0 },
            { 3.0 },
            { 2.0 },
            { 5.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(6.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_MultiChannelWindow_ComputesPerChannelIndependently()
    {
        // Ch0: |3-1| + |5-3| = 4.  Ch1: |4-2| + |6-4| = 4.
        using var block = new WaveformLength(name: "WL", desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0 },
            { 3.0, 4.0 },
            { 5.0, 6.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(4.0, result[0], 1e-9);
        Assert.AreEqual(4.0, result[1], 1e-9);
    }

    [TestMethod]
    public void OnReceive_ConstantSignal_ProducesZero()
    {
        // No sample-to-sample change => every difference is 0 => WL = 0.
        using var block = new WaveformLength(name: "WL", desiredRate: 0);
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
    public void OnReceive_NegativeAndPositiveSwings_UsesAbsoluteDifferences()
    {
        // |-2-1| = 3, |2-(-2)| = 4 => WL = 7 (magnitude of the difference, not signed).
        using var block = new WaveformLength(name: "WL", desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0 },
            { -2.0 },
            { 2.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(7.0, result[0], 1e-9);
    }
}
