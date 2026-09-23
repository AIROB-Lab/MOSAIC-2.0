using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.MachineLearning.Classification;

namespace MOSAIC.Tests.Components.MachineLearning.Classifier;

/// <summary>
/// Tests for <see cref="LinearSvmClassifier"/>: a multi-class linear SVM trained by SGD on the
/// multi-class hinge loss (one weight row per class).
///
/// Model: scores = W·x + b; Predict = argmax(scores); probabilities = stable softmax(scores).
/// Per-sample step (when margin s_correct − s_wrong &lt; 1): the correct row moves toward +η·x,
/// the top wrong row toward −η·x, then all rows decay by (1 − η·λ).
///
/// Determinism note: unlike the other online classifiers, the new-row weight init in
/// <c>EnsureInit</c> draws from an UNSEEDED <c>new Random()</c>, so trained models are NOT
/// bit-reproducible across instances — cross-instance determinism is therefore not asserted.
/// Two deterministic facts anchor the exact known-answer tests without touching that RNG:
///  1. A fresh (or freshly Reset) model has W == null, so Predict returns 0 and
///     PredictProbabilities returns the exact uniform distribution 1/NumClasses for ANY input.
///  2. The init magnitude is tiny (each weight in [−0.005, 0.005)), so on strongly separated
///     data the η·x update terms dominate and classification outcomes are robust to the init.
/// Distribution invariants (length, non-negativity, sum-to-one, argmax == Predict) hold for any W.
/// </summary>
[TestClass]
public class LinearSvmClassifierTests
{
    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    // ---------------------------------------------------------------- fresh-model surface

    [TestMethod]
    public void Constructor_InitializesDimensionsAndUntrainedState()
    {
        // Dimensions are stored verbatim; a model that has seen no samples is not trained
        // (IsTrained requires W != null AND at least one processed sample).
        var model = new LinearSvmClassifier(inputDim: 4, numClasses: 3);

        Assert.AreEqual(4, model.InputDimension);
        Assert.AreEqual(3, model.OutputDimension); // OutputDimension == NumClasses
        Assert.AreEqual(3, model.NumClasses);
        Assert.IsFalse(model.IsTrained);           // no Update yet
    }

    [TestMethod]
    public void Predict_FreshModel_ReturnsClassZero()
    {
        // Untrained: W is null, so Predict short-circuits to class 0 regardless of the input.
        var model = new LinearSvmClassifier(inputDim: 3, numClasses: 3);
        var x = Vec(2.0, -5.0, 1.0);

        var predicted = model.Predict(x);

        Assert.AreEqual(0, predicted); // integer label => exact match
    }

    [TestMethod]
    public void PredictProbabilities_FreshModel_ReturnsUniformDistribution()
    {
        // Untrained: W is null, so the method returns Repeat(1/NumClasses, NumClasses) for ANY x.
        // NumClasses = 4 => each probability is exactly 0.25 (this branch never inspects x).
        var model = new LinearSvmClassifier(inputDim: 3, numClasses: 4);
        var x = Vec(9.0, -3.0, 7.0); // deliberately non-zero to show x is irrelevant here

        var probs = model.PredictProbabilities(x);

        Assert.AreEqual(4, probs.Length);
        Assert.AreEqual(0.25, probs[0], 1e-9); // 1/4
        Assert.AreEqual(0.25, probs[1], 1e-9); // 1/4
        Assert.AreEqual(0.25, probs[2], 1e-9); // 1/4
        Assert.AreEqual(0.25, probs[3], 1e-9); // 1/4
        Assert.AreEqual(1.0, probs.Sum(), 1e-9);
    }

    [TestMethod]
    public void Confidence_FreshModel_EqualsUniformProbability()
    {
        // Confidence is the max class probability. On a fresh model the distribution is uniform
        // 1/NumClasses, so its maximum is 1/NumClasses. NumClasses = 5 => 0.2.
        var model = new LinearSvmClassifier(inputDim: 2, numClasses: 5);
        var x = Vec(1.0, -1.0);

        var confidence = model.Confidence(x);

        Assert.AreEqual(0.2, confidence, 1e-9);                             // 1/5
        Assert.AreEqual(model.PredictProbabilities(x).Max(), confidence, 1e-12);
    }

    // ---------------------------------------------------------------- single update

    [TestMethod]
    public void Update_OnFreshModel_MarksTrainedAndMovesMassTowardTrueLabel()
    {
        // "before" is taken on the fresh model => uniform, so p(label) = 1/3.
        // A single hinge step on (x, label) raises the correct row's score and lowers the top
        // wrong row's score; with a sizeable x this makes `label` the clear argmax, so its
        // softmax probability rises well above 1/3. The tiny (±0.005) init cannot offset the
        // η·(|x|² + 1) ≈ 0.5·15 gain, so the increase is robust to the unseeded init.
        var model = new LinearSvmClassifier(inputDim: 3, numClasses: 3, learningRate: 0.5, lambda: 0.0);
        var x = Vec(1.0, 2.0, 3.0);
        const int label = 2;

        var before = model.PredictProbabilities(x)[label]; // 1/3 (fresh, uniform)
        model.Update(x, label);
        var after = model.PredictProbabilities(x)[label];

        Assert.IsTrue(model.IsTrained);                             // one sample consumed => trained
        Assert.AreEqual(1.0 / 3.0, before, 1e-9);                   // fresh model was uniform
        Assert.IsTrue(after > before,
            $"expected p(label) to increase after a hinge step, got before={before}, after={after}");
    }

    // ---------------------------------------------------------------- separable training

    [TestMethod]
    public void Update_TrainOnSeparableData_ClassifiesBothClassesCorrectly()
    {
        // Two strongly separated 2-D clusters: class 0 near [+3,+3], class 1 near [-3,-3].
        // With λ = 0 the step is constant (η = 0.1, no decay), and the discriminant direction
        // d = w0 − w1 accumulates ±2η·x per violated sample, converging after the first sample.
        // The tiny init contributes < ±0.05 to a score of magnitude ~3.6, so both the training
        // points and unseen points on each side are classified correctly.
        var model = new LinearSvmClassifier(inputDim: 2, numClasses: 2, learningRate: 0.1, lambda: 0.0);

        var pos = Vec(3.0, 3.0);   // class 0
        var neg = Vec(-3.0, -3.0); // class 1
        for (int i = 0; i < 40; i++)
        {
            model.Update(pos, 0);
            model.Update(neg, 1);
        }

        Assert.AreEqual(0, model.Predict(pos));          // training point, class 0
        Assert.AreEqual(1, model.Predict(neg));          // training point, class 1
        Assert.AreEqual(0, model.Predict(Vec(5.0, 1.0))); // unseen positive-side point
        Assert.AreEqual(1, model.Predict(Vec(-1.0, -5.0))); // unseen negative-side point
    }

    [TestMethod]
    public void TrainBatch_OnSeparableData_ClassifiesCorrectly()
    {
        // Same idea driven through the batch entry point (shuffled SGD over `epochs` passes).
        // Three well-separated points per class in opposite quadrants remain linearly separable,
        // so after 50 epochs every training point is classified into its own class.
        var model = new LinearSvmClassifier(inputDim: 2, numClasses: 2, learningRate: 0.1, lambda: 0.0);

        var features = new List<Vector<double>>
        {
            Vec(3.0, 3.0), Vec(2.0, 4.0), Vec(4.0, 2.0),      // class 0
            Vec(-3.0, -3.0), Vec(-2.0, -4.0), Vec(-4.0, -2.0), // class 1
        };
        var labels = new List<int> { 0, 0, 0, 1, 1, 1 };

        model.TrainBatch(features, labels, epochs: 50);

        Assert.IsTrue(model.IsTrained);
        for (int i = 0; i < features.Count; i++)
            Assert.AreEqual(labels[i], model.Predict(features[i]),
                $"sample {i} should classify as its training label {labels[i]}");
    }

    [TestMethod]
    public void PredictProbabilities_AfterTraining_IsValidDistributionAndMatchesPrediction()
    {
        // For any weights, the stable softmax yields a valid distribution: length == NumClasses,
        // every entry in [0,1], entries sum to exactly 1. Softmax is monotonic in the scores, so
        // its argmax equals Predict's argmax (both derive from the same W·x + b).
        var model = new LinearSvmClassifier(inputDim: 2, numClasses: 3, learningRate: 0.1, lambda: 0.0);
        model.Update(Vec(3.0, 3.0), 0);
        model.Update(Vec(-3.0, 3.0), 1);
        model.Update(Vec(0.0, -3.0), 2);
        var x = Vec(2.5, 2.5);

        var probs = model.PredictProbabilities(x);
        var predicted = model.Predict(x);

        Assert.AreEqual(3, probs.Length);
        foreach (var p in probs)
        {
            Assert.IsTrue(p >= 0.0, $"probability {p} was negative");
            Assert.IsTrue(p <= 1.0, $"probability {p} exceeded 1");
        }
        Assert.AreEqual(1.0, probs.Sum(), 1e-9);                 // softmax normalizes to Σ = 1
        Assert.IsTrue(predicted >= 0 && predicted < 3, "class index out of range");
        Assert.AreEqual(Array.IndexOf(probs, probs.Max()), predicted); // argmax(probs) == Predict
    }

    // ---------------------------------------------------------------- reset

    [TestMethod]
    public void Reset_AfterTraining_RestoresUntrainedState()
    {
        // Reset nulls W and b and zeroes the sample count, returning the model to the fresh state:
        // IsTrained false, Predict 0, and the uniform 1/NumClasses distribution again.
        var model = new LinearSvmClassifier(inputDim: 2, numClasses: 2, learningRate: 0.1, lambda: 0.0);
        for (int i = 0; i < 20; i++)
        {
            model.Update(Vec(3.0, 3.0), 0);
            model.Update(Vec(-3.0, -3.0), 1);
        }
        Assert.IsTrue(model.IsTrained); // precondition: training happened

        model.Reset();

        Assert.IsFalse(model.IsTrained);
        var x = Vec(3.0, 3.0);
        Assert.AreEqual(0, model.Predict(x));                   // W == null => class 0
        var probs = model.PredictProbabilities(x);
        Assert.AreEqual(2, probs.Length);
        Assert.AreEqual(0.5, probs[0], 1e-9);                   // 1/2
        Assert.AreEqual(0.5, probs[1], 1e-9);                   // 1/2
    }

    // ---------------------------------------------------------------- class growth

    [TestMethod]
    public void Update_WithLabelBeyondNumClasses_GrowsClassCount()
    {
        // Update sets NumClasses = Max(NumClasses, label + 1). Starting from 2 classes and
        // training a sample with label 3 grows the model to 4 classes (labels 0..3).
        var model = new LinearSvmClassifier(inputDim: 2, numClasses: 2);

        model.Update(Vec(1.0, 1.0), label: 3);

        Assert.AreEqual(4, model.NumClasses);      // Max(2, 3 + 1) = 4
        Assert.AreEqual(4, model.OutputDimension); // stays in lockstep with NumClasses
        Assert.IsTrue(model.IsTrained);
    }

    // ---------------------------------------------------------------- guards

    [TestMethod]
    public void TrainBatch_EmptyOrMismatchedData_ThrowsArgumentException()
    {
        // Guard: features must be non-empty and equal length to labels.
        var model = new LinearSvmClassifier(inputDim: 2, numClasses: 2);
        var oneFeature = new List<Vector<double>> { Vec(1.0, 2.0) };

        Assert.ThrowsExactly<ArgumentException>(
            () => model.TrainBatch(new List<Vector<double>>(), new List<int>()));          // empty
        Assert.ThrowsExactly<ArgumentException>(
            () => model.TrainBatch(oneFeature, new List<int> { 0, 1 }));                    // length mismatch
    }
}
