using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.Analytics;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.Analytics;

/// <summary>
/// Known-answer tests for <see cref="MetricsExtractor"/>.
/// Input is a <see cref="Matrix{T}"/> (rows = samples, columns = channels); the block publishes a
/// <see cref="Vector{T}"/> holding one metric value per channel. The metric to compute is selected
/// via the constructor (or the <c>Metric</c> property), and every ComputeXXX method is private, so
/// each metric is exercised through the public surface with <see cref="BlockHarness.CaptureVector"/>.
/// Each expected value is derived by hand from the metric's mathematical definition.
/// </summary>
[TestClass]
public class MetricExtractorTests
{
    /// <summary>Reusable single-channel window { 3, -4, 0, 5 } used across several metrics.</summary>
    private static Matrix<double> SingleChannelWindow() =>
        Matrix<double>.Build.DenseOfArray(new double[,]
        {
            {  3.0 },
            { -4.0 },
            {  0.0 },
            {  5.0 },
        });

    [TestMethod]
    public void OnReceive_Rms_ComputesRootMeanSquare()
    {
        // RMS = √(Σx²/N). x = {3,-4,0,5} => (9+16+0+25)/4 = 50/4 = 12.5 => √12.5 ≈ 3.5355339059.
        using var block = new MetricsExtractor(name: "M", metric: MetricType.RMS, desiredRate: 0);

        var result = BlockHarness.CaptureVector(block, SingleChannelWindow());

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(Math.Sqrt(12.5), result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_Mav_ComputesMeanAbsoluteValue()
    {
        // MAV = Σ|x|/N. (|3|+|-4|+|0|+|5|)/4 = (3+4+0+5)/4 = 12/4 = 3.
        using var block = new MetricsExtractor(name: "M", metric: MetricType.MAV, desiredRate: 0);

        var result = BlockHarness.CaptureVector(block, SingleChannelWindow());

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(3.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_Iemg_ComputesSumOfAbsoluteValues()
    {
        // IEMG = Σ|x| = 3+4+0+5 = 12 (no division by N).
        using var block = new MetricsExtractor(name: "M", metric: MetricType.IEMG, desiredRate: 0);

        var result = BlockHarness.CaptureVector(block, SingleChannelWindow());

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(12.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_Ssi_ComputesSimpleSquareIntegral()
    {
        // SSI = Σx² = 9+16+0+25 = 50.
        using var block = new MetricsExtractor(name: "M", metric: MetricType.SSI, desiredRate: 0);

        var result = BlockHarness.CaptureVector(block, SingleChannelWindow());

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(50.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_Var_ComputesPopulationVariance()
    {
        // VAR = Σ(x−μ)²/N. μ = (3−4+0+5)/4 = 4/4 = 1.
        // deviations: (3−1)=2, (−4−1)=−5, (0−1)=−1, (5−1)=4 => squares 4+25+1+16 = 46.
        // VAR = 46/4 = 11.5.
        using var block = new MetricsExtractor(name: "M", metric: MetricType.VAR, desiredRate: 0);

        var result = BlockHarness.CaptureVector(block, SingleChannelWindow());

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(11.5, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_Std_ComputesPopulationStandardDeviation()
    {
        // STD = √VAR = √11.5 ≈ 3.3911649916 (μ=1, Σ(x−μ)²=46, N=4 as in the VAR case).
        using var block = new MetricsExtractor(name: "M", metric: MetricType.STD, desiredRate: 0);

        var result = BlockHarness.CaptureVector(block, SingleChannelWindow());

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(Math.Sqrt(11.5), result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_Mean_ComputesArithmeticMean()
    {
        // MEAN = Σx/N = (3−4+0+5)/4 = 4/4 = 1.
        using var block = new MetricsExtractor(name: "M", metric: MetricType.MEAN, desiredRate: 0);

        var result = BlockHarness.CaptureVector(block, SingleChannelWindow());

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(1.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_Peak_ComputesMaxAbsoluteValue()
    {
        // PEAK = max(|x|) = max(3,4,0,5) = 5. (|-4| loses to |5|; sign is discarded.)
        using var block = new MetricsExtractor(name: "M", metric: MetricType.PEAK, desiredRate: 0);

        var result = BlockHarness.CaptureVector(block, SingleChannelWindow());

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(5.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_P2P_ComputesPeakToPeakRange()
    {
        // P2P = max(x) − min(x) = 5 − (−4) = 9 (signed values, not absolute).
        using var block = new MetricsExtractor(name: "M", metric: MetricType.P2P, desiredRate: 0);

        var result = BlockHarness.CaptureVector(block, SingleChannelWindow());

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(9.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_Log_ComputesLogDetectorOverNonZeroSamples()
    {
        // LOG = e^(Σ ln|x| / K) over samples with |x| > 1e-10.
        // x = {1,2,4,8} => ln1+ln2+ln4+ln8 = 0 + ln2 + 2ln2 + 3ln2 = 6·ln2, K = 4.
        // exponent = 6·ln2/4 = 1.5·ln2 => e^(1.5 ln2) = 2^1.5 = 2√2 ≈ 2.8284271247.
        using var block = new MetricsExtractor(name: "M", metric: MetricType.LOG, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0 },
            { 2.0 },
            { 4.0 },
            { 8.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(Math.Pow(2.0, 1.5), result[0], 1e-6);
    }

    [TestMethod]
    public void OnReceive_Zscore_ComputesLastSampleZScore()
    {
        // ZSCORE = (x[N−1] − μ) / σ over the window.
        // x = {2,4,4,4,6,6,4,4} ... use a simple window: x = {1,2,3,4}.
        // μ = 10/4 = 2.5. Σ(x−μ)² = 2.25+0.25+0.25+2.25 = 5 => VAR = 5/4 = 1.25 => σ = √1.25.
        // last sample = 4 => z = (4 − 2.5)/√1.25 = 1.5/1.1180339887 ≈ 1.3416407865.
        using var block = new MetricsExtractor(name: "M", metric: MetricType.ZSCORE, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0 },
            { 2.0 },
            { 3.0 },
            { 4.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(1.5 / Math.Sqrt(1.25), result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_MultiChannel_ComputesEachChannelIndependently()
    {
        // RMS per channel over a 3×2 window.
        // Ch0 = {3,4,0} => √((9+16+0)/3) = √(25/3) ≈ 2.8867513459.
        // Ch1 = {0,0,6} => √((0+0+36)/3) = √12 ≈ 3.4641016151.
        using var block = new MetricsExtractor(name: "M", metric: MetricType.RMS, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 3.0, 0.0 },
            { 4.0, 0.0 },
            { 0.0, 6.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(Math.Sqrt(25.0 / 3.0), result[0], 1e-9);
        Assert.AreEqual(Math.Sqrt(12.0), result[1], 1e-9);
    }

    [TestMethod]
    public void OnReceive_AllZeroInput_RmsIsZero()
    {
        // Σx²/N = 0 => √0 = 0. Edge case: no divide-by-zero because N = rows = 3 ≠ 0.
        using var block = new MetricsExtractor(name: "M", metric: MetricType.RMS, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 0.0 },
            { 0.0 },
            { 0.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(0.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_ConstantSignal_VarianceIsZero()
    {
        // Constant signal => μ = 7, every deviation 0 => VAR = Σ0/N = 0.
        using var block = new MetricsExtractor(name: "M", metric: MetricType.VAR, desiredRate: 0);
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
    public void OnReceive_AllZeroInput_LogGuardReturnsZero()
    {
        // Divide-by-zero / log(0) guard: every |x| ≤ 1e-10 => validCount = 0 => returns 0.0
        // (instead of e^(Σ/0) = NaN).
        using var block = new MetricsExtractor(name: "M", metric: MetricType.LOG, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 0.0 },
            { 0.0 },
            { 0.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(0.0, result[0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_ConstantSignal_ZscoreGuardReturnsZero()
    {
        // σ < 1e-12 for a constant window => guard returns 0.0 instead of dividing by zero.
        using var block = new MetricsExtractor(name: "M", metric: MetricType.ZSCORE, desiredRate: 0);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 5.0 },
            { 5.0 },
            { 5.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(0.0, result[0], 1e-9);
    }

    [TestMethod]
    public void Constructor_DefaultMetric_IsRms()
    {
        // Default metric is RMS; ensure the default path publishes the RMS value.
        // x = {3,4} => √((9+16)/2) = √12.5 ≈ 3.5355339059.
        using var block = new MetricsExtractor(name: "M");
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 3.0 },
            { 4.0 },
        });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(MetricType.RMS, block.Metric);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(Math.Sqrt(12.5), result[0], 1e-9);
    }
}
