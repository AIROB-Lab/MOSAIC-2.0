using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.SignalProcessing;

/// <summary>
/// Known-answer tests for <see cref="DepthFilter"/>: an ultrasound depth filter that filters along
/// the depth (sample) axis of each frame independently, resetting filter state per frame.
///
/// Input/output convention (from the source): a <see cref="Vector{T}"/> is one depth line of length
/// nSamples; a <see cref="Matrix{T}"/> is [nChannels rows × nSamples cols] and each row is filtered
/// along its columns. The block publishes the same type and shape it received.
///
/// Two behaviours are pinned here:
///
/// 1. Passthrough. When <see cref="DepthFilter.IsEnabled"/> is <see langword="false"/>, the block
///    republishes the input unchanged (same values, same shape).
///
/// 2. FIR lowpass transform. A 3-tap windowed-sinc Hamming lowpass at fs=1000, fc=250 (normalized
///    cutoff fc/fs = 0.25) has hand-derivable coefficients. With middle index m = (3-1)/2 = 1:
///
///      raw[0]: n = -1 → sinc = sin(2π·0.25·(-1)) / (π·(-1)) = sin(-π/2)/(-π) = (-1)/(-π) = 1/π,
///              Hamming w[0] = 0.54 - 0.46·cos(0)   = 0.08          → raw[0] = 0.08/π
///      raw[1]: n =  0 → sinc = 2·fc = 0.5,
///              Hamming w[1] = 0.54 - 0.46·cos(π)   = 1.0           → raw[1] = 0.5
///      raw[2]: n =  1 → sinc = sin(π/2)/π = 1/π,
///              Hamming w[2] = 0.54 - 0.46·cos(2π)  = 0.08          → raw[2] = 0.08/π
///
///    sum = 0.5 + 0.16/π = 0.5509295817894065; normalized to unity DC gain (Σ b = 1):
///      b0 = b2 = (0.08/π)/sum = 0.04622149860240625,  b1 = 0.5/sum = 0.9075570027951875.
///
///    The FIR is Reset before each frame, so the delay line starts all-zero and
///    y[n] = Σ_k b[k]·x[n-k] with samples before the frame treated as 0.
/// </summary>
[TestClass]
public class DepthFilterTests
{
    // Hand-derived normalized 3-tap FIR lowpass coefficients (fs=1000, fc=250). See class remarks.
    private const double B0 = 0.04622149860240625; // (0.08/π) / (0.5 + 0.16/π)
    private const double B1 = 0.9075570027951875;  //  0.5     / (0.5 + 0.16/π)
    private const double B2 = B0;                   // symmetric taps: b2 == b0

    [TestMethod]
    public void OnReceive_Disabled_PublishesMatrixUnchanged()
    {
        // IsEnabled = false => passthrough: values and shape identical to the input.
        using var block = new DepthFilter(name: "DF") { IsEnabled = false };
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0, 3.0 },
            { 4.0, 5.0, 6.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(3, result.ColumnCount);
        for (int r = 0; r < input.RowCount; r++)
        for (int c = 0; c < input.ColumnCount; c++)
            Assert.AreEqual(input[r, c], result[r, c], 1e-9); // exact copy, no arithmetic
    }

    [TestMethod]
    public void OnReceive_Disabled_PublishesVectorUnchanged()
    {
        // Passthrough for the single depth-line (Vector) case.
        using var block = new DepthFilter(name: "DF") { IsEnabled = false };
        var input = Vector<double>.Build.Dense(new[] { -2.0, 7.0, 0.5 });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(-2.0, result[0], 1e-9);
        Assert.AreEqual(7.0, result[1], 1e-9);
        Assert.AreEqual(0.5, result[2], 1e-9);
    }

    [TestMethod]
    public void OnReceive_FirLowpassVector_AppliesConvolutionPerSample()
    {
        // 3-tap FIR lowpass, fs=1000, fc=250. Frame x = [4, 8, 12], delay line reset to zero.
        //   y0 = b0·4                          = 0.184885994409625
        //   y1 = b0·8 + b1·4  = 4·(2·b0 + b1)  = 4·1 = 4   (2·b0 + b1 = b0 + b1 + b2 = 1)
        //   y2 = b0·12 + b1·8 + b2·4 = 8·(2·b0 + b1) = 8·1 = 8
        using var block = new DepthFilter(
            name: "DF",
            filterType: FilterType.Lowpass,
            implementation: FilterImplementation.FIR,
            cutoffLow: 250,
            firTaps: 3,
            depthSampleRate: 1000);
        var input = Vector<double>.Build.Dense(new[] { 4.0, 8.0, 12.0 });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(B0 * 4.0, result[0], 1e-6); // 0.184885994409625
        Assert.AreEqual(4.0, result[1], 1e-6);      // 4·(2b0 + b1) = 4
        Assert.AreEqual(8.0, result[2], 1e-6);      // 8·(2b0 + b1) = 8
    }

    [TestMethod]
    public void OnReceive_FirLowpassMatrix_FiltersEachRowIndependently()
    {
        // Matrix is [channels × samples]; each row is filtered along its columns with the FIR
        // state reset per row. Row0 = [4,8,12] (same as the vector case); Row1 = [0,0,0] → all 0.
        using var block = new DepthFilter(
            name: "DF",
            filterType: FilterType.Lowpass,
            implementation: FilterImplementation.FIR,
            cutoffLow: 250,
            firTaps: 3,
            depthSampleRate: 1000);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 4.0, 8.0, 12.0 },
            { 0.0, 0.0, 0.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(3, result.ColumnCount);

        // Row 0: identical to the single-vector result (per-row reset => no carry-over from a prior row).
        Assert.AreEqual(B0 * 4.0, result[0, 0], 1e-6); // 0.184885994409625
        Assert.AreEqual(4.0, result[0, 1], 1e-6);
        Assert.AreEqual(8.0, result[0, 2], 1e-6);

        // Row 1: an all-zero depth line convolves to all zeros.
        Assert.AreEqual(0.0, result[1, 0], 1e-6);
        Assert.AreEqual(0.0, result[1, 1], 1e-6);
        Assert.AreEqual(0.0, result[1, 2], 1e-6);
    }

    [TestMethod]
    public void OnReceive_FirLowpassConstantSignal_ReproducesDcAfterTransient()
    {
        // Unity DC gain (Σ b = 1): once the 3-tap FIR fully overlaps a constant, output = input.
        // For x = [c, c, c, c]:
        //   y0 = b0·c              (transient: delay line filling from zero)
        //   y1 = (b0 + b1)·c       (transient)
        //   y2 = (b0 + b1 + b2)·c = c·1 = c   (steady state)
        //   y3 = (b0 + b1 + b2)·c = c·1 = c
        const double c = 5.0;
        using var block = new DepthFilter(
            name: "DF",
            filterType: FilterType.Lowpass,
            implementation: FilterImplementation.FIR,
            cutoffLow: 250,
            firTaps: 3,
            depthSampleRate: 1000);
        var input = Vector<double>.Build.Dense(new[] { c, c, c, c });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(4, result.Count);
        Assert.AreEqual(c * B0, result[0], 1e-6);        // b0·c
        Assert.AreEqual(c * (B0 + B1), result[1], 1e-6); // (b0+b1)·c
        Assert.AreEqual(c, result[2], 1e-6);             // (b0+b1+b2)·c = c
        Assert.AreEqual(c, result[3], 1e-6);             // (b0+b1+b2)·c = c
    }

    [TestMethod]
    public void OnReceive_EnabledByDefault_TransformsRatherThanPassesThrough()
    {
        // Guard on the enabled/disabled branch: with IsEnabled true (constructor default), a
        // non-trivial FIR lowpass must change the first sample (y0 = b0·x0, and b0 ≈ 0.0462 ≠ 1).
        using var block = new DepthFilter(
            name: "DF",
            filterType: FilterType.Lowpass,
            implementation: FilterImplementation.FIR,
            cutoffLow: 250,
            firTaps: 3,
            depthSampleRate: 1000);

        Assert.IsTrue(block.IsEnabled); // constructor default is enabled

        var input = Vector<double>.Build.Dense(new[] { 10.0, 10.0, 10.0 });
        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(10.0 * B0, result[0], 1e-6); // 0.4622... — filtering happened
        Assert.AreNotEqual(10.0, result[0], 1e-3);   // not a passthrough of the input value
    }
}
