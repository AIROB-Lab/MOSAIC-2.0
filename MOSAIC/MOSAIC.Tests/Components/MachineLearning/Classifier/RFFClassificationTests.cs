using System;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.MachineLearning.Interfaces;
using MOSAIC.MachineLearning.Classification;

namespace MOSAIC.Tests.Components.MachineLearning.Classifier;

/// <summary>
/// Tests for <see cref="RFFClassification"/>: a Random Fourier Features wrapper that maps inputs
/// through φ(x) = sqrt(2/D)·cos(Ω·x + β) and delegates to an inner <see cref="OnlineSoftmax"/>.
///
/// The random maps Ω, β are drawn from MathNet distributions seeded by the ctor <c>seed</c>, and the
/// inner softmax initialises its weights from a fixed internal seed (42). Every code path is therefore
/// fully deterministic, but the exact Ω/β draws are not reproducible by hand — so the assertions below
/// target properties that follow from the definitions: the softmax invariants (probabilities are a
/// non-negative distribution summing to 1), the wrapper's exact delegation to the inner classifier
/// (Predict == argmax(PredictProbabilities), Confidence == max(PredictProbabilities)), same-seed
/// determinism, exact restoration via Reset / SetState, and correct labels on well-separated data.
/// delta 1e-6 is used for the softmax-sum invariant (transcendental math) and 1e-9 where the compared
/// values come from the identical computation and must be bit-for-bit reproducible.
/// </summary>
[TestClass]
public class RFFClassificationTests
{
    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    // ---------------------------------------------------------------- construction / properties

    [TestMethod]
    public void Constructor_SetsDimensionsSeedAndFeatureSpace()
    {
        // Properties are copied straight from the ctor args; OutputDimension mirrors NumClasses,
        // and FeatureDimension is the RFF map width D (the inner classifier's input dimension).
        var clf = new RFFClassification(inputDim: 4, numClasses: 3, featureDim: 50, seed: 7);

        Assert.AreEqual(4, clf.InputDimension);
        Assert.AreEqual(3, clf.NumClasses);
        Assert.AreEqual(3, clf.OutputDimension); // OutputDimension => inner.OutputDimension => NumClasses
        Assert.AreEqual(50, clf.FeatureDimension);
        Assert.AreEqual(7, clf.Seed);
    }

    [DataTestMethod]
    [DataRow(0, 2, 1.0, 10)]   // inputDim <= 0
    [DataRow(2, 1, 1.0, 10)]   // numClasses <= 1
    [DataRow(2, 2, 0.0, 10)]   // sigma <= 0
    [DataRow(2, 2, 1.0, 0)]    // featureDim <= 0
    public void Constructor_InvalidArguments_ThrowArgumentOutOfRange(int inputDim, int numClasses, double sigma, int featureDim)
    {
        // Each guard in the ctor rejects a non-positive / degenerate argument.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RFFClassification(inputDim, numClasses, sigma: sigma, featureDim: featureDim, seed: 1));
    }

    // ---------------------------------------------------------------- softmax output invariants

    [TestMethod]
    public void PredictProbabilities_FreshModel_HasNumClassesEntriesFormingADistribution()
    {
        // φ(x) feeds a softmax over NumClasses logits => exactly NumClasses probabilities in [0,1]
        // that sum to 1, regardless of the (random) feature map.
        var clf = new RFFClassification(inputDim: 3, numClasses: 4, featureDim: 32, seed: 11);

        var probs = clf.PredictProbabilities(Vec(0.2, -0.5, 1.0));

        Assert.AreEqual(4, probs.Length);
        Assert.AreEqual(1.0, probs.Sum(), 1e-6); // softmax normalisation
        foreach (var p in probs)
        {
            Assert.IsTrue(p >= 0.0 && p <= 1.0, $"probability {p} outside [0,1]");
        }
    }

    [TestMethod]
    public void Predict_ReturnsArgmaxOfProbabilities_WithinClassRange()
    {
        // Predict delegates to inner.Predict => softmax(...).MaximumIndex(), i.e. the argmax of the
        // very same probability vector PredictProbabilities returns (first max on ties).
        var clf = new RFFClassification(inputDim: 3, numClasses: 5, featureDim: 40, seed: 21);
        var x = Vec(1.5, -2.0, 0.75);

        var probs = clf.PredictProbabilities(x);
        var predicted = clf.Predict(x);

        int expectedArgmax = 0;
        for (int i = 1; i < probs.Length; i++)
            if (probs[i] > probs[expectedArgmax]) expectedArgmax = i;

        Assert.AreEqual(expectedArgmax, predicted);
        Assert.IsTrue(predicted >= 0 && predicted < 5, "label out of [0, NumClasses)");
    }

    [TestMethod]
    public void Confidence_EqualsMaximumProbability()
    {
        // Confidence delegates to inner.Confidence => softmax(...).Maximum(): the same computation as
        // the max of PredictProbabilities, so the two are bit-for-bit identical.
        var clf = new RFFClassification(inputDim: 3, numClasses: 4, featureDim: 40, seed: 33);
        var x = Vec(0.3, 0.9, -1.2);

        var probs = clf.PredictProbabilities(x);
        var confidence = clf.Confidence(x);

        Assert.AreEqual(probs.Max(), confidence, 1e-9);
        Assert.IsTrue(confidence >= 1.0 / 4 - 1e-9, "max prob cannot be below the uniform value 1/NumClasses");
        Assert.IsTrue(confidence <= 1.0 + 1e-9);
    }

    // ---------------------------------------------------------------- determinism / seeding

    [TestMethod]
    public void SameSeed_ProducesIdenticalProbabilities()
    {
        // Same seed => identical Ω, β (same seeded RNG stream), and the inner softmax always seeds its
        // initial weights from 42 => two independent instances are indistinguishable before training.
        var a = new RFFClassification(inputDim: 3, numClasses: 3, featureDim: 48, seed: 99);
        var b = new RFFClassification(inputDim: 3, numClasses: 3, featureDim: 48, seed: 99);
        var x = Vec(0.4, -0.6, 1.1);

        var pa = a.PredictProbabilities(x);
        var pb = b.PredictProbabilities(x);

        Assert.AreEqual(pa.Length, pb.Length);
        for (int i = 0; i < pa.Length; i++)
            Assert.AreEqual(pa[i], pb[i], 1e-12); // reproduced by identical computation
    }

    // ---------------------------------------------------------------- reset / state

    [TestMethod]
    public void Reset_AfterTraining_RestoresInitialPredictions()
    {
        // Reset delegates to inner.Reset, which regenerates W from the fixed seed 42 and zeroes b/count.
        // Ω, β are immutable, so φ(x) is unchanged => the post-reset distribution matches the initial one.
        var clf = new RFFClassification(inputDim: 2, numClasses: 3, featureDim: 40, seed: 5);
        var x = Vec(0.7, -0.4);
        var before = clf.PredictProbabilities(x);

        for (int epoch = 0; epoch < 25; epoch++)
        {
            clf.Update(Vec(-2.0, 0.0), 0);
            clf.Update(Vec(2.0, 0.0), 1);
            clf.Update(Vec(0.0, 2.0), 2);
        }
        clf.Reset();
        var after = clf.PredictProbabilities(x);

        Assert.AreEqual(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++)
            Assert.AreEqual(before[i], after[i], 1e-9);
    }

    [TestMethod]
    public void GetState_ReturnsSoftmaxStateInFeatureSpace()
    {
        // The inner classifier operates on the D-dimensional feature space, so its weight matrix is
        // NumClasses x FeatureDimension with a NumClasses-length bias.
        var clf = new RFFClassification(inputDim: 3, numClasses: 4, featureDim: 20, seed: 5);

        SoftmaxState state = clf.GetState();

        Assert.IsNotNull(state);
        Assert.AreEqual(4, state.W.RowCount);      // NumClasses
        Assert.AreEqual(20, state.W.ColumnCount);  // FeatureDimension
        Assert.AreEqual(4, state.B.Count);
    }

    [TestMethod]
    public void SetState_RestoresPreviouslyCapturedPredictions()
    {
        // Snapshot -> train -> restore snapshot must reproduce the snapshot's distribution exactly,
        // because SetState overwrites the inner W/b and φ(x) is unchanged.
        var clf = new RFFClassification(inputDim: 2, numClasses: 3, featureDim: 40, seed: 8);
        var x = Vec(0.5, -0.9);
        var probs0 = clf.PredictProbabilities(x);
        SoftmaxState snapshot = clf.GetState();

        for (int epoch = 0; epoch < 30; epoch++)
        {
            clf.Update(Vec(-2.0, 0.0), 0);
            clf.Update(Vec(2.0, 0.0), 1);
            clf.Update(Vec(0.0, 2.0), 2);
        }
        clf.SetState(snapshot);
        var probs1 = clf.PredictProbabilities(x);

        Assert.AreEqual(probs0.Length, probs1.Length);
        for (int i = 0; i < probs0.Length; i++)
            Assert.AreEqual(probs0[i], probs1[i], 1e-9);
    }

    // ---------------------------------------------------------------- learning behaviour

    [TestMethod]
    public void Training_OnWellSeparatedData_LearnsCorrectLabels()
    {
        // Two clusters far apart along x1. RFF + softmax, trained for many epochs with a fixed seed,
        // is fully deterministic and must classify its own well-separated training points correctly.
        var clf = new RFFClassification(
            inputDim: 2, numClasses: 2, learningRate: 0.1, lambda: 0.0,
            sigma: 1.5, featureDim: 128, seed: 2024);

        var class0A = Vec(-3.0, 0.0);
        var class0B = Vec(-2.5, 0.5);
        var class1A = Vec(3.0, 0.0);
        var class1B = Vec(2.5, -0.5);

        for (int epoch = 0; epoch < 400; epoch++)
        {
            clf.Update(class0A, 0);
            clf.Update(class0B, 0);
            clf.Update(class1A, 1);
            clf.Update(class1B, 1);
        }

        Assert.AreEqual(0, clf.Predict(class0A));
        Assert.AreEqual(0, clf.Predict(class0B));
        Assert.AreEqual(1, clf.Predict(class1A));
        Assert.AreEqual(1, clf.Predict(class1B));
    }

    // ---------------------------------------------------------------- input-dimension guards

    [TestMethod]
    public void Predict_WrongInputDimension_Throws()
    {
        // Predict validates x.Count == InputDimension before transforming.
        var clf = new RFFClassification(inputDim: 3, numClasses: 2, featureDim: 10, seed: 1);

        Assert.ThrowsExactly<ArgumentException>(() => clf.Predict(Vec(1.0, 2.0))); // 2 != 3
    }

    [TestMethod]
    public void Update_WrongInputDimension_Throws()
    {
        // Update applies the same dimension guard before feeding φ(x) to the inner classifier.
        var clf = new RFFClassification(inputDim: 3, numClasses: 2, featureDim: 10, seed: 1);

        Assert.ThrowsExactly<ArgumentException>(() => clf.Update(Vec(1.0, 2.0, 3.0, 4.0), 0)); // 4 != 3
    }

    // ---------------------------------------------------------------- wrapping ctor

    [TestMethod]
    public void WrapConstructor_InnerInputDimMismatch_Throws()
    {
        // The wrapping ctor requires inner.InputDimension == featureDim (the inner must live in φ-space).
        var inner = new OnlineSoftmax(inputDim: 8, numClasses: 2); // InputDimension 8

        Assert.ThrowsExactly<ArgumentException>(
            () => new RFFClassification(inputDim: 3, inner: inner, featureDim: 10, seed: 1)); // 8 != 10
    }

    [TestMethod]
    public void WrapConstructor_MatchingInner_ProducesValidDistribution()
    {
        // When the inner classifier's input dimension matches featureDim, the wrapper is well-formed
        // and produces a proper NumClasses-length distribution.
        var inner = new OnlineSoftmax(inputDim: 16, numClasses: 3);
        var clf = new RFFClassification(inputDim: 4, inner: inner, sigma: 1.0, featureDim: 16, seed: 3);

        var probs = clf.PredictProbabilities(Vec(1.0, 2.0, 3.0, 4.0));

        Assert.AreEqual(3, clf.NumClasses);
        Assert.AreEqual(16, clf.FeatureDimension);
        Assert.AreEqual(3, probs.Length);
        Assert.AreEqual(1.0, probs.Sum(), 1e-6);
    }
}
