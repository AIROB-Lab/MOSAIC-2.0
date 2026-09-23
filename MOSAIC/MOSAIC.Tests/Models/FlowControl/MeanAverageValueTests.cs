using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.FlowControl;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="MeanAverageValue"/>.
/// MAV per channel = (1/N) · Σ |x[i]| (rows = samples, columns = channels).
/// </summary>
[TestClass]
public class MeanAverageValueTests
{
    [TestMethod]
    public void OnReceive_MultiChannelWindow_PublishesMeanAbsolutePerChannel()
    {
        // Ch0: (|1|+|3|+|5|)/3 = 3.  Ch1: (|2|+|4|+|6|)/3 = 4.
        using var block = new MeanAverageValue(name: "MAV", desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0 },
            { 3.0, 4.0 },
            { 5.0, 6.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(3.0, result[0], 1e-9);
        Assert.AreEqual(4.0, result[1], 1e-9);
    }

    [TestMethod]
    public void OnReceive_NegativeSamples_UsesAbsoluteValue()
    {
        // (|-1| + |-2| + |3|) / 3 = 6 / 3 = 2.
        using var block = new MeanAverageValue(name: "MAV", desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { -1.0 },
            { -2.0 },
            { 3.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(2.0, result[0], 1e-9);
    }
}
