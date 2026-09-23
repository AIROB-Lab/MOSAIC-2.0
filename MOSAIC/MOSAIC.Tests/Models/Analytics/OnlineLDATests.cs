using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.Analytics;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.Analytics;

/// <summary>
/// Known-answer tests for <see cref="OnlineLDA"/>, an incremental Linear Discriminant Analysis block.
///
/// The projection matrix W is seeded with <c>DenseMatrix.Build.Random(d, k)</c> on the first sample
/// (UNSEEDED), and is only overwritten with data-driven eigenvectors once <c>UpdateProjection</c>
/// runs (every <c>reorthEvery</c> samples, and only with ≥ 2 classes present). So any expected value
/// that depends on W before that point is non-deterministic. Every assertion below is therefore
/// derived only from parts that are INDEPENDENT of the random W, or from robust LDA invariants:
///
///   • Welford global mean: after the first sample x₀, μ = x₀ exactly (starts at 0, μ ← μ + (x₀−μ)/1);
///     after N samples μ equals the arithmetic mean of the inputs.
///   • First projection: on sample 1 the centred input xc = x₀ − μ = 0, so the published projection
///     y = Wᵀ·xc = Wᵀ·0 = 0 for ANY W — deterministically the zero vector of length k.
///   • Sample / class counting and the stability gate (_n ≥ minStableCount AND ≥ 2 classes) are pure
///     bookkeeping.
///   • Constructor / init guards (k range, uninitialised/wrong-dim Project).
///   • With exactly two classes the between-class scatter Sb is rank 1, so Sw⁻¹·Sb has a single
///     non-zero eigenvalue: the normalised separability scores must be ≈ [1, 0].
///
/// The block publishes a boxed <c>(string label, Vector&lt;double&gt;)</c> tuple — the class label plus
/// the k-dimensional projection — so <see cref="BlockHarness.TryCapture"/> is used to grab it and the
/// value is unboxed to that tuple type. Input is a <see cref="Vector{T}"/> (one observation) or a
/// <see cref="Matrix{T}"/> (rows = observations, processed sequentially). The current class label is
/// supplied through <see cref="OnlineLDA.SetCurrentLabel"/>.
/// </summary>
[TestClass]
public class OnlineLDATests
{
    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    /// <summary>Sets the label then feeds one observation through the public receive entry point.</summary>
    private static void Feed(OnlineLDA block, string label, Vector<double> x)
    {
        block.SetCurrentLabel(label);
        block.ReceiveInput(sender: block, value: x);
    }

    // ---------------------------------------------------------------- constructor guards

    [TestMethod]
    public void Constructor_KLessThanTwo_ThrowsArgumentOutOfRange()
    {
        // Guard: k must be 2 or 3 (for 2-D/3-D visualisation). k = 1 is rejected.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new OnlineLDA(name: "lda", desiredRate: 0, k: 1));
    }

    [TestMethod]
    public void Constructor_KGreaterThanThree_ThrowsArgumentOutOfRange()
    {
        // Guard: k = 4 is out of the allowed {2, 3} range.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new OnlineLDA(name: "lda", desiredRate: 0, k: 4));
    }

    [TestMethod]
    public void Constructor_DefaultParams_ExposesEmptyInitialState()
    {
        // Default k = 2. Before any sample: no samples, no classes, not stable, global mean empty (d = 0).
        using var block = new OnlineLDA(name: "lda", desiredRate: 0);

        Assert.AreEqual(2, block.ComponentCount);   // default k
        Assert.AreEqual(0L, block.SampleCount);     // nothing processed
        Assert.AreEqual(0, block.ClassCount);       // no classes seen
        Assert.IsFalse(block.IsStable);             // stability needs samples + 2 classes
        Assert.AreEqual(0, block.GlobalMean.Count); // μ starts as a length-0 vector (d = 0)
    }

    // ---------------------------------------------------------------- first-sample behaviour

    [TestMethod]
    public void Process_FirstSample_PublishesZeroProjectionWithLabel()
    {
        // On the first sample x₀, Welford makes the global mean μ = x₀ exactly, so the centred input
        // xc = x₀ − μ = 0. Hence y = Wᵀ·xc = Wᵀ·0 = 0 for any (random) W. The block publishes the
        // tuple (label, y): label echoes the current capture label, y is the k-length zero vector.
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2);
        block.SetCurrentLabel("A");
        var x0 = Vec(4.0, 7.0, -2.0); // d = 3

        bool got = BlockHarness.TryCapture(block, x0, out var published, timeoutMs: 2000);

        Assert.IsTrue(got, "Block did not publish within the timeout.");
        Assert.IsInstanceOfType(published, typeof((string, Vector<double>)));
        var (label, y) = ((string, Vector<double>))published!;
        Assert.AreEqual("A", label);              // published label matches the capture label
        Assert.AreEqual(2, y.Count);              // projection length == k
        Assert.AreEqual(0.0, y[0], 1e-9);         // xc = 0 => y = 0
        Assert.AreEqual(0.0, y[1], 1e-9);
    }

    [TestMethod]
    public void Process_FirstSample_GlobalMeanEqualsInputAndCountsOne()
    {
        // Welford on the first sample: μ starts at 0, then μ ← μ + (x₀ − μ)/1 = x₀. One Vector
        // observation increments the sample count once and registers exactly one class.
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2);
        var x0 = Vec(4.0, 7.0, -2.0);

        Feed(block, "A", x0);

        Assert.AreEqual(1L, block.SampleCount);
        Assert.AreEqual(1, block.ClassCount);       // just class "A"
        Assert.AreEqual(3, block.GlobalMean.Count); // d = 3
        Assert.AreEqual(4.0, block.GlobalMean[0], 1e-9);  // μ = x₀
        Assert.AreEqual(7.0, block.GlobalMean[1], 1e-9);
        Assert.AreEqual(-2.0, block.GlobalMean[2], 1e-9);
    }

    [TestMethod]
    public void Process_MultipleSamples_GlobalMeanIsArithmeticMean()
    {
        // Welford global mean of {[2,4], [4,8], [6,12]} equals the arithmetic mean:
        //   n=1: μ = [2,4]
        //   n=2: μ = [2,4] + ([4,8]-[2,4])/2 = [3,6]
        //   n=3: μ = [3,6] + ([6,12]-[3,6])/3 = [4,8].
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2);

        Feed(block, "A", Vec(2.0, 4.0));
        Feed(block, "A", Vec(4.0, 8.0));
        Feed(block, "A", Vec(6.0, 12.0));

        Assert.AreEqual(3L, block.SampleCount);
        Assert.AreEqual(2, block.GlobalMean.Count);
        Assert.AreEqual(4.0, block.GlobalMean[0], 1e-9);  // (2+4+6)/3
        Assert.AreEqual(8.0, block.GlobalMean[1], 1e-9);  // (4+8+12)/3
    }

    [TestMethod]
    public void Process_DistinctLabels_ClassCountCountsUniqueLabels()
    {
        // Two different labels => two class-statistics entries and two captured clusters.
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2);

        Feed(block, "A", Vec(1.0, 1.0));
        Feed(block, "B", Vec(9.0, 9.0));
        Feed(block, "A", Vec(2.0, 2.0)); // "A" again, no new class

        Assert.AreEqual(3L, block.SampleCount);
        Assert.AreEqual(2, block.ClassCount);                       // {A, B}
        Assert.IsTrue(block.CapturedClusters.ContainsKey("A"));
        Assert.IsTrue(block.CapturedClusters.ContainsKey("B"));
    }

    [TestMethod]
    public void Process_MatrixInput_ProcessesEachRowAsSample()
    {
        // A Matrix is processed row-by-row; each row is one sample. A 4-row matrix advances
        // SampleCount to 4 (pure integer bookkeeping, independent of W). No label set => all rows
        // land in the single "default" class.
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 0.0 },
            { 0.0, 1.0 },
            { 2.0, 2.0 },
            { 3.0, 1.0 },
        });

        block.ReceiveInput(sender: block, value: input);

        Assert.AreEqual(4L, block.SampleCount);  // 4 rows == 4 samples
        Assert.AreEqual(1, block.ClassCount);    // all in "default"
    }

    // ---------------------------------------------------------------- Project guards

    [TestMethod]
    public void Project_BeforeInitialisation_ThrowsInvalidOperation()
    {
        // Project requires _d != 0. Called before any sample, _d == 0 => guard throws.
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2);
        var x = Vec(1.0, 2.0);

        Assert.ThrowsExactly<InvalidOperationException>(() => block.Project(x));
    }

    [TestMethod]
    public void Project_WrongDimension_ThrowsArgument()
    {
        // After one 2-D sample, _d == 2. Projecting a 3-D vector violates the dimension guard.
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2);
        Feed(block, "A", Vec(1.0, 2.0)); // initialises d = 2

        Assert.ThrowsExactly<ArgumentException>(() => block.Project(Vec(1.0, 2.0, 3.0)));
    }

    // ---------------------------------------------------------------- SetCurrentLabel

    [TestMethod]
    public void SetCurrentLabel_NonEmpty_EnablesCapturingAndRegistersCluster()
    {
        // A non-empty label turns capturing on, records the class name, and creates a cluster entry
        // with the first available colour (#FF6B6B is index 0 of the palette).
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2);

        block.SetCurrentLabel("gestureA");

        Assert.IsTrue(block.IsCapturing);
        Assert.AreEqual("gestureA", block.CurrentClassName);
        Assert.IsTrue(block.CapturedClusters.ContainsKey("gestureA"));
        Assert.AreEqual("#FF6B6B", block.ClusterColors["gestureA"]); // first palette colour
    }

    [TestMethod]
    public void SetCurrentLabel_Empty_DisablesCapturing()
    {
        // Setting an empty label clears the capturing state and the current class name.
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2);
        block.SetCurrentLabel("A"); // enable first

        block.SetCurrentLabel("");

        Assert.IsFalse(block.IsCapturing);
        Assert.AreEqual("", block.CurrentClassName);
    }

    // ---------------------------------------------------------------- stability gate

    [TestMethod]
    public void IsStable_SingleClassAtThreshold_RemainsFalse()
    {
        // Stability needs BOTH _n ≥ minStableCount AND ≥ 2 classes. With only the "default" class,
        // reaching the sample threshold is not enough — the flag stays false.
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2, minStableCount: 4);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0 },
            { 3.0, 4.0 },
            { 5.0, 6.0 },
            { 7.0, 8.0 },
        });

        block.ReceiveInput(sender: block, value: input); // 4 samples, 1 class

        Assert.AreEqual(4L, block.SampleCount);
        Assert.AreEqual(1, block.ClassCount);
        Assert.IsFalse(block.IsStable); // only one class => never stable
    }

    [TestMethod]
    public void IsStable_TwoClassesAtThreshold_BecomesTrue()
    {
        // With minStableCount = 4 and two classes present, the 4th sample satisfies both conditions
        // (_n = 4 ≥ 4 and 2 classes) => stability flips true.
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2, minStableCount: 4);

        Feed(block, "A", Vec(0.0, 0.0));
        Feed(block, "B", Vec(9.0, 9.0));
        Feed(block, "A", Vec(0.5, 0.2));
        Feed(block, "B", Vec(9.5, 9.2)); // n = 4, 2 classes

        Assert.AreEqual(4L, block.SampleCount);
        Assert.AreEqual(2, block.ClassCount);
        Assert.IsTrue(block.IsStable);
    }

    // ---------------------------------------------------------------- Reset

    [TestMethod]
    public void Reset_AfterSamples_ClearsState()
    {
        // Reset returns the model to the uninitialised state: no samples, no classes, not stable,
        // global mean emptied (d = 0), and Project invalid again.
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2, minStableCount: 2);
        Feed(block, "A", Vec(1.0, 2.0));
        Feed(block, "B", Vec(8.0, 9.0));
        Assert.IsTrue(block.IsStable);

        block.Reset();

        Assert.AreEqual(0L, block.SampleCount);
        Assert.AreEqual(0, block.ClassCount);
        Assert.IsFalse(block.IsStable);
        Assert.AreEqual(0, block.GlobalMean.Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => block.Project(Vec(1.0, 2.0)));
    }

    // ---------------------------------------------------------------- LDA separation invariant

    [TestMethod]
    public void Training_TwoSeparatedClasses_YieldsRankOneSeparabilityAndSeparatedProjection()
    {
        // Feed two tight, far-apart 3-D clusters (centres (0,0,0) and (20,20,20)), interleaved, so
        // that UpdateProjection runs (reorthEvery = 20, ≥ 2 classes) and stability is reached
        // (minStableCount = 40). Small per-axis jitter keeps the within-class scatter Sw full rank.
        //
        // With exactly TWO classes the between-class scatter Sb = Σ n_c (μ_c − μ)(μ_c − μ)ᵀ is rank 1
        // (both (μ_c − μ) lie on the single line through the two class means). Therefore Sw⁻¹·Sb has a
        // single non-zero eigenvalue, and the normalised separability scores must be ≈ [1, 0]:
        // essentially all class separation is captured by the first discriminant LD1.
        using var block = new OnlineLDA(name: "lda", desiredRate: 0, k: 2,
            reorthEvery: 20, minStableCount: 40, regularization: 1e-4);

        for (int i = 0; i < 30; i++)
        {
            // Deterministic per-axis jitter (no RNG): different periods per axis keep Sw non-degenerate.
            double j0 = ((i % 5) - 2) * 0.2;   // {-0.4..0.4}
            double j1 = ((i % 3) - 1) * 0.3;   // {-0.3..0.3}
            double j2 = ((i % 4) - 1.5) * 0.2; // {-0.3..0.3}

            Feed(block, "A", Vec(0.0 + j0, 0.0 + j1, 0.0 + j2));
            Feed(block, "B", Vec(20.0 + j0, 20.0 + j1, 20.0 + j2));
        }

        Assert.AreEqual(60L, block.SampleCount);
        Assert.AreEqual(2, block.ClassCount);
        Assert.IsTrue(block.IsStable);

        var scores = block.SeparabilityScores;
        Assert.AreEqual(2, scores.Count);                         // one score per component (k)
        Assert.AreEqual(1.0, scores[0] + scores[1], 1e-6);        // normalised => scores sum to 1
        Assert.IsTrue(scores[0] > 0.9,                            // rank-1 Sb => LD1 dominates
            $"Expected LD1 to dominate separability, got scores[0] = {scores[0]:F4}");

        // The learned projection must keep the two classes far apart. Project the two class centres
        // and confirm the projected points are well separated (distance ≫ within-class jitter).
        var pA = block.Project(Vec(0.0, 0.0, 0.0));
        var pB = block.Project(Vec(20.0, 20.0, 20.0));
        Assert.AreEqual(2, pA.Count);                             // reduced to k = 2 dimensions
        var diff = pA - pB;
        double dist = Math.Sqrt(diff.DotProduct(diff));
        Assert.IsTrue(dist > 5.0, $"Projected class centres too close: dist = {dist:F3}");
    }
}
