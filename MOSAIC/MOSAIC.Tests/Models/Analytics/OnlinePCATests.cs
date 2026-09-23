using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.Analytics;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.Analytics;

/// <summary>
/// Known-answer tests for <see cref="OnlinePCA"/>, an incremental (Oja's-rule) PCA block.
///
/// The loadings matrix W is seeded with <c>DenseMatrix.Build.Random(d, k)</c> on first sample
/// (UNSEEDED), so any expected value that depends on W is non-deterministic and cannot be a
/// known answer. Every assertion below is therefore derived only from the parts of the algorithm
/// that are INDEPENDENT of W:
///
///   • Mean update (Welford): after the very first sample x₀, μ = x₀ exactly (starts at 0,
///     μ ← μ + (x₀ − μ)/1 = x₀).
///   • First projection: on sample 1 the centred input is xc = x₀ − μ = 0, so the published
///     projection y = Wᵀ·xc = Wᵀ·0 = 0 for ANY W. Deterministically the zero vector of length k.
///   • Reconstruct at the mean: x̂ = W·0 + μ = μ, again independent of W.
///   • Sample counting, component count, and the stability threshold (_n ≥ minStableCount) are
///     pure integer bookkeeping.
///   • Constructor / initialisation guards (k range, uninitialised Project/Reconstruct).
///
/// Input is a <see cref="Vector{T}"/> (single observation) or <see cref="Matrix{T}"/> (rows =
/// samples, processed sequentially). The block publishes a <see cref="Vector{T}"/> — the
/// k-dimensional projection of the current sample.
/// </summary>
[TestClass]
public class OnlinePCATests
{
    [TestMethod]
    public void Constructor_KLessThanTwo_ThrowsArgumentOutOfRange()
    {
        // Guard: k must be 2 or 3 (for 2-D/3-D visualisation). k = 1 is rejected.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new OnlinePCA(name: "pca", desiredRate: 0, k: 1));
    }

    [TestMethod]
    public void Constructor_KGreaterThanThree_ThrowsArgumentOutOfRange()
    {
        // Guard: k = 4 is out of the allowed {2, 3} range.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new OnlinePCA(name: "pca", desiredRate: 0, k: 4));
    }

    [TestMethod]
    public void Constructor_DefaultK_ExposesComponentCountTwo()
    {
        // Default constructor parameter k = 2 => ComponentCount == 2. No sample needed;
        // _k is fixed at construction.
        using var block = new OnlinePCA(name: "pca", desiredRate: 0);

        Assert.AreEqual(2, block.ComponentCount);
    }

    [TestMethod]
    public void Process_FirstSample_PublishesZeroProjection()
    {
        // On the first sample x₀, Welford makes μ = x₀ exactly, so the centred input
        // xc = x₀ − μ = 0. Hence y = Wᵀ·xc = Wᵀ·0 = 0 for any (random) W.
        // Expected published vector: [0, 0] (k = 2), independent of the RNG.
        using var block = new OnlinePCA(name: "pca", desiredRate: 0, k: 2);
        var x0 = Vector<double>.Build.Dense(new[] { 4.0, 7.0, -2.0 }); // d = 3

        var projection = BlockHarness.CaptureVector(block, x0);

        Assert.AreEqual(2, projection.Count);        // length == k
        Assert.AreEqual(0.0, projection[0], 1e-6);   // xc = 0 => y = 0
        Assert.AreEqual(0.0, projection[1], 1e-6);
    }

    [TestMethod]
    public void Process_FirstSample_MeanEqualsInput()
    {
        // Welford on the first sample: μ starts at 0, then μ ← μ + (x₀ − μ)/1 = x₀.
        // So Mean must equal the input vector exactly after one Vector sample.
        using var block = new OnlinePCA(name: "pca", desiredRate: 0, k: 2);
        var x0 = Vector<double>.Build.Dense(new[] { 4.0, 7.0, -2.0 });

        _ = BlockHarness.CaptureVector(block, x0);

        Assert.AreEqual(3, block.Mean.Count);        // d = 3
        Assert.AreEqual(4.0, block.Mean[0], 1e-6);   // μ = x₀
        Assert.AreEqual(7.0, block.Mean[1], 1e-6);
        Assert.AreEqual(-2.0, block.Mean[2], 1e-6);
    }

    [TestMethod]
    public void Process_FirstSample_SampleCountIsOne()
    {
        // A single Vector observation increments _n exactly once.
        using var block = new OnlinePCA(name: "pca", desiredRate: 0, k: 2);
        var x0 = Vector<double>.Build.Dense(new[] { 1.0, 2.0 });

        _ = BlockHarness.CaptureVector(block, x0);

        Assert.AreEqual(1L, block.SampleCount);
    }

    [TestMethod]
    public void Process_MatrixInput_ProcessesEachRowAsASample()
    {
        // A Matrix is processed row-by-row; each row is one sample. A 4-row matrix
        // advances SampleCount to 4 (pure integer bookkeeping, independent of W).
        // The rows are processed synchronously in one burst while the async publish pump
        // races behind, so WHICH row's projection the harness captures is not deterministic;
        // only the count (4) and the projection's length (k) are asserted here.
        using var block = new OnlinePCA(name: "pca", desiredRate: 0, k: 2);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 0.0 },
            { 0.0, 1.0 },
            { 2.0, 2.0 },
            { 3.0, 1.0 },
        });

        var projection = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(2, projection.Count);   // published projection length == k
        Assert.AreEqual(4L, block.SampleCount); // 4 rows == 4 samples
    }

    [TestMethod]
    public void IsStable_BelowThreshold_RemainsFalse()
    {
        // Stability flips true only once _n ≥ minStableCount. With minStableCount = 3 and a
        // 2-row matrix (n = 2 < 3), the model must still report unstable.
        using var block = new OnlinePCA(name: "pca", desiredRate: 0, k: 2, minStableCount: 3);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0 },
            { 3.0, 4.0 },
        });

        _ = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(2L, block.SampleCount);
        Assert.IsFalse(block.IsStable);
    }

    [TestMethod]
    public void IsStable_AtThreshold_BecomesTrue()
    {
        // With minStableCount = 3, feeding exactly 3 rows makes _n = 3 ≥ 3, so IsStable is set.
        using var block = new OnlinePCA(name: "pca", desiredRate: 0, k: 2, minStableCount: 3);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0 },
            { 3.0, 4.0 },
            { 5.0, 6.0 },
        });

        _ = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(3L, block.SampleCount);
        Assert.IsTrue(block.IsStable);
    }

    [TestMethod]
    public void Project_BeforeInitialisation_ThrowsInvalidOperation()
    {
        // Project requires _d != 0. Called before any sample, _d == 0 => guard throws.
        using var block = new OnlinePCA(name: "pca", desiredRate: 0, k: 2);
        var x = Vector<double>.Build.Dense(new[] { 1.0, 2.0 });

        Assert.ThrowsExactly<InvalidOperationException>(() => block.Project(x));
    }

    [TestMethod]
    public void Reconstruct_BeforeInitialisation_ThrowsInvalidOperation()
    {
        // Reconstruct also requires _d != 0. Before any sample it must throw.
        using var block = new OnlinePCA(name: "pca", desiredRate: 0, k: 2);
        var y = Vector<double>.Build.Dense(new[] { 0.0, 0.0 });

        Assert.ThrowsExactly<InvalidOperationException>(() => block.Reconstruct(y));
    }

    [TestMethod]
    public void Reconstruct_AtOrigin_ReturnsMean()
    {
        // After one sample x₀, μ = x₀. Reconstruct(0) = W·0 + μ = μ = x₀, independent of W.
        // (Reconstruct does not mutate state, so μ is still x₀ when we call it.)
        using var block = new OnlinePCA(name: "pca", desiredRate: 0, k: 2);
        var x0 = Vector<double>.Build.Dense(new[] { 5.0, -3.0, 8.0 }); // d = 3
        _ = BlockHarness.CaptureVector(block, x0);

        var xHat = block.Reconstruct(Vector<double>.Build.Dense(new[] { 0.0, 0.0 })); // y = 0 (k = 2)

        Assert.AreEqual(3, xHat.Count);            // reconstruction has d = 3 components
        Assert.AreEqual(5.0, xHat[0], 1e-6);       // = μ[0] = x₀[0]
        Assert.AreEqual(-3.0, xHat[1], 1e-6);      // = μ[1]
        Assert.AreEqual(8.0, xHat[2], 1e-6);       // = μ[2]
    }

    [TestMethod]
    public void Project_AtMean_ReturnsZeroVector()
    {
        // After one sample x₀, μ = x₀. Project(x₀) centres to xc = x₀ − μ = 0, so
        // y = Wᵀ·0 = 0 for any W. Project does not mutate state.
        using var block = new OnlinePCA(name: "pca", desiredRate: 0, k: 2);
        var x0 = Vector<double>.Build.Dense(new[] { 5.0, -3.0, 8.0 });
        _ = BlockHarness.CaptureVector(block, x0);

        var y = block.Project(x0); // projecting the mean itself

        Assert.AreEqual(2, y.Count);          // length == k
        Assert.AreEqual(0.0, y[0], 1e-6);
        Assert.AreEqual(0.0, y[1], 1e-6);
    }

    [TestMethod]
    public void Reset_AfterSamples_ClearsSampleCountAndStability()
    {
        // Reset restores _n = 0 and _isStable = false. minStableCount = 1 so the single
        // sample marks the model stable before we reset it.
        using var block = new OnlinePCA(name: "pca", desiredRate: 0, k: 2, minStableCount: 1);
        var x0 = Vector<double>.Build.Dense(new[] { 1.0, 2.0 });
        _ = BlockHarness.CaptureVector(block, x0);
        Assert.AreEqual(1L, block.SampleCount);
        Assert.IsTrue(block.IsStable);

        block.Reset();

        Assert.AreEqual(0L, block.SampleCount);   // counter cleared
        Assert.IsFalse(block.IsStable);           // stability flag cleared
    }
}
