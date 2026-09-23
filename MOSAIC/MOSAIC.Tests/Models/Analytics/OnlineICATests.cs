using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.Analytics;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.Analytics;

/// <summary>
/// Known-answer tests for <see cref="OnlineICA"/>: online (incremental) ICA.
///
/// The private ICA-learning internals (contrast gradient, FastICA W update, whitening
/// eigendecomposition) are exercised only after the warm-up phase completes, and their
/// outputs depend on an adaptive learning-rate schedule plus periodic orthonormalization —
/// there is no clean closed-form value to assert against a single sample. So these tests
/// pin down the parts that ARE exactly hand-derivable through the PUBLIC surface:
///
///   * Welford online mean:        μₙ = μₙ₋₁ + (xₙ − μₙ₋₁)/n              (public <see cref="OnlineICA.Mean"/>)
///   * Welford online covariance:  C = M₂/(n−1) + 1e-8·I  (M₂ = Σ outer(δ, δ'))  (public <see cref="OnlineICA.Covariance"/>)
///   * Warm-up projection output:  y = W_white·(x − μ),  W_white init = k×d selection (rows i pick column i)
///   * <see cref="OnlineICA.Transform"/> = W·(W_white·(x − μ)), with W = I_k pre-learning
///   * constructor guard, init guards, and trivial-property invariants.
///
/// During warm-up (n &lt; warmupSamples) the block only estimates statistics and publishes the
/// whitened-centered projection; W stays identity. We keep warmupSamples large so every sample
/// we feed stays in that deterministic regime. Rows = samples, columns = channels.
/// </summary>
[TestClass]
public class OnlineICATests
{
    // A vector helper matching the template's construction style.
    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    [TestMethod]
    public void Ctor_KLessThanOne_ThrowsArgumentOutOfRange()
    {
        // Constructor requires k >= 1; k = 0 must throw ArgumentOutOfRangeException.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new OnlineICA(name: "ICA", desiredRate: 0, k: 0));
    }

    [TestMethod]
    public void Ctor_SetsComponentCountAndInitialFlags()
    {
        // k is echoed by ComponentCount; before any data no samples processed and,
        // with a non-zero warm-up, warm-up is not yet complete.
        using var block = new OnlineICA(name: "ICA", desiredRate: 0, k: 3, warmupSamples: 100);

        Assert.AreEqual(3, block.ComponentCount);
        Assert.AreEqual(0L, block.SampleCount);      // nothing processed yet
        Assert.IsFalse(block.WarmupComplete);        // warmupSamples = 100 > 0
        Assert.IsFalse(block.IsStable);              // stability needs minStableCount samples
    }

    [TestMethod]
    public void Transform_BeforeAnyData_ThrowsInvalidOperation()
    {
        // _d == 0 until the first sample initializes the model, so Transform must reject.
        using var block = new OnlineICA(name: "ICA", desiredRate: 0, k: 2, warmupSamples: 100);

        Assert.ThrowsExactly<InvalidOperationException>(() => block.Transform(Vec(1.0, 2.0)));
    }

    [TestMethod]
    public void Process_FirstWarmupSample_PublishesCenteredZeroVector()
    {
        // First sample x1 = [2,4,6]. Welford: mean = 0 + (x-0)/1 = x, so x - mean = 0.
        // Warm-up output y = W_white·(x - mean) = W_white·0 = 0, dimension k = 2.
        using var block = new OnlineICA(name: "ICA", desiredRate: 0, k: 2, warmupSamples: 100);
        var x1 = Vec(2.0, 4.0, 6.0);

        var y = BlockHarness.CaptureVector(block, x1);

        Assert.AreEqual(2, y.Count);                 // output dimensionality = k
        Assert.AreEqual(0.0, y[0], 1e-9);            // centered input is exactly zero
        Assert.AreEqual(0.0, y[1], 1e-9);
    }

    [TestMethod]
    public void Process_SecondWarmupSample_PublishesWhitenedCenteredProjection()
    {
        // k = d = 2 so the init whitening matrix is the 2x2 identity (rows i pick column i).
        using var block = new OnlineICA(name: "ICA", desiredRate: 0, k: 2, warmupSamples: 100);

        // Sample 1: x1 = [1,3]. n=1 => mean = [1,3], centered = 0 => output [0,0] (ignored here).
        BlockHarness.CaptureVector(block, Vec(1.0, 3.0));

        // Sample 2: x2 = [3,7]. n=2 => mean = [1,3] + ([3,7]-[1,3])/2 = [1,3] + [1,2] = [2,5].
        //   centered = [3,7] - [2,5] = [1,2].  y = I2·[1,2] = [1,2].
        var y = BlockHarness.CaptureVector(block, Vec(3.0, 7.0));

        Assert.AreEqual(2, y.Count);
        Assert.AreEqual(1.0, y[0], 1e-9);            // 3 - 2 = 1
        Assert.AreEqual(2.0, y[1], 1e-9);            // 7 - 5 = 2
    }

    [TestMethod]
    public void Mean_AfterTwoSamples_MatchesWelfordRunningMean()
    {
        // Welford mean of {[1,3], [3,7]} = arithmetic mean = [2,5].
        using var block = new OnlineICA(name: "ICA", desiredRate: 0, k: 2, warmupSamples: 100);

        BlockHarness.CaptureVector(block, Vec(1.0, 3.0));   // n=1: mean = [1,3]
        BlockHarness.CaptureVector(block, Vec(3.0, 7.0));   // n=2: mean = [2,5]

        var mean = block.Mean;

        Assert.AreEqual(2, mean.Count);
        Assert.AreEqual(2.0, mean[0], 1e-9);   // (1+3)/2
        Assert.AreEqual(5.0, mean[1], 1e-9);   // (3+7)/2
        Assert.AreEqual(2L, block.SampleCount); // two samples consumed
    }

    [TestMethod]
    public void Covariance_AfterTwoSamples_MatchesWelfordSampleCovariance()
    {
        // Data {[1,3], [3,7]}, mean [2,5], deviations [-1,-2] and [+1,+2].
        // M2 = Σ outer(δ_old, δ_new). Welford accumulates:
        //   n=1: δ = [1,3], δ' = 0            => contributes 0.
        //   n=2: δ = [2,4], δ' = [1,2]        => outer = [[2,4],[4,8]].
        // C = M2/(n-1) = [[2,4],[4,8]] (with +1e-8 diagonal regularization; symmetric).
        using var block = new OnlineICA(name: "ICA", desiredRate: 0, k: 2, warmupSamples: 100);

        BlockHarness.CaptureVector(block, Vec(1.0, 3.0));
        BlockHarness.CaptureVector(block, Vec(3.0, 7.0));

        var c = block.Covariance;

        Assert.AreEqual(2, c.RowCount);
        Assert.AreEqual(2, c.ColumnCount);
        Assert.AreEqual(2.0, c[0, 0], 1e-6);   // Var(ch0)·(n-1) contribution: 2
        Assert.AreEqual(4.0, c[0, 1], 1e-6);   // Cov(ch0,ch1): 4
        Assert.AreEqual(4.0, c[1, 0], 1e-6);   // symmetric
        Assert.AreEqual(8.0, c[1, 1], 1e-6);   // Var(ch1)·(n-1) contribution: 8
    }

    [TestMethod]
    public void Transform_DuringWarmup_EqualsWhitenedCenteredInput()
    {
        // Transform(x) = W·(W_white·(x - μ)). Before warm-up completes, W = I_k and
        // W_white is the init identity (k=d=2), so Transform reduces to (x - μ).
        using var block = new OnlineICA(name: "ICA", desiredRate: 0, k: 2, warmupSamples: 100);

        BlockHarness.CaptureVector(block, Vec(1.0, 3.0));   // n=1
        BlockHarness.CaptureVector(block, Vec(3.0, 7.0));   // n=2 => mean = [2,5]

        // Transform does NOT mutate state: apply the current unmixing to a fresh point.
        var t = block.Transform(Vec(3.0, 7.0));

        Assert.AreEqual(2, t.Count);
        Assert.AreEqual(1.0, t[0], 1e-9);   // 3 - 2 = 1
        Assert.AreEqual(2.0, t[1], 1e-9);   // 7 - 5 = 2
    }

    [TestMethod]
    public void Reset_AfterProcessing_ClearsSampleCountAndMean()
    {
        // Reset returns the model to the uninitialized state: _d = 0, _n = 0, mean cleared.
        using var block = new OnlineICA(name: "ICA", desiredRate: 0, k: 2, warmupSamples: 100);
        BlockHarness.CaptureVector(block, Vec(1.0, 3.0));
        BlockHarness.CaptureVector(block, Vec(3.0, 7.0));

        block.Reset();

        Assert.AreEqual(0L, block.SampleCount);      // counter reset
        Assert.AreEqual(0, block.Mean.Count);        // mean vector emptied (d = 0)
        // After reset, Transform is invalid again because the model is uninitialized.
        Assert.ThrowsExactly<InvalidOperationException>(() => block.Transform(Vec(1.0, 2.0)));
    }

    [TestMethod]
    public void Process_WarmupDisabled_MarksWarmupCompleteImmediately()
    {
        // warmupSamples = 0 => warm-up is considered complete from the very first sample
        // (EnsureInit sets _warmupComplete = (_warmupSamples == 0)).
        using var block = new OnlineICA(name: "ICA", desiredRate: 0, k: 2, warmupSamples: 0);

        var y = BlockHarness.CaptureVector(block, Vec(5.0, 9.0));

        Assert.AreEqual(2, y.Count);                 // still publishes a k-vector
        Assert.IsTrue(block.WarmupComplete);         // no warm-up gate
        Assert.AreEqual(1L, block.SampleCount);
    }
}
