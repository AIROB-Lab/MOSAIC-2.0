using System;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.MachineLearning.Classification;

namespace MOSAIC.Tests.Components.MachineLearning.Classifier;

/// <summary>
/// Tests for <see cref="OnlineSoftmax"/>: an online softmax classifier trained by SGD.
///
/// Model: p(y=c|x) = softmax(W·x + b), with numerically-stable softmax
///   softmax(z)_c = exp(z_c - max z) / Σ_k exp(z_k - max z).
///
/// The constructor calls Reset(), which zeroes the bias (b = 0) and fills W with Xavier
/// values drawn from a HARDCODED <c>new Random(42)</c> stream. Two facts make exact
/// known-answers derivable without depending on the platform RNG stream:
///  1. b = 0 for every fresh (or freshly Reset) model.
///  2. For the zero input vector x = 0, the logits are W·0 + b = 0 regardless of W, so the
///     softmax collapses to the uniform distribution 1/NumClasses. This is exact arithmetic.
/// Distribution invariants (length, non-negativity, sum-to-one) hold for ANY input and any W,
/// so they are asserted on arbitrary inputs too.
/// </summary>
[TestClass]
public class OnlineSoftmaxTests
{
    [TestMethod]
    public void PredictProbabilities_ZeroInput_ReturnsUniformDistribution()
    {
        // x = 0 => logits = W·0 + b = 0 (b is zero on a fresh model) => every logit equal.
        // Stable softmax of an all-zero logit vector: exp(0-0)/Σ exp(0-0) = 1/K for each of K classes.
        // K = 4 => each probability is exactly 0.25.
        var model = new OnlineSoftmax(inputDim: 3, numClasses: 4);
        var x = Vector<double>.Build.Dense(3, 0.0);

        var probs = model.PredictProbabilities(x);

        Assert.AreEqual(4, probs.Length);
        Assert.AreEqual(0.25, probs[0], 1e-9); // 1/4
        Assert.AreEqual(0.25, probs[1], 1e-9); // 1/4
        Assert.AreEqual(0.25, probs[2], 1e-9); // 1/4
        Assert.AreEqual(0.25, probs[3], 1e-9); // 1/4
    }

    [TestMethod]
    public void PredictProbabilities_ArbitraryInput_IsValidProbabilityDistribution()
    {
        // Regardless of the (random) weights, a stable softmax always yields a valid distribution:
        // length == NumClasses, every entry in [0, 1], and the entries sum to exactly 1.
        var model = new OnlineSoftmax(inputDim: 5, numClasses: 3);
        var x = Vector<double>.Build.Dense(new[] { 1.5, -2.0, 0.75, 4.0, -1.25 });

        var probs = model.PredictProbabilities(x);

        Assert.AreEqual(3, probs.Length);
        foreach (var p in probs)
        {
            Assert.IsTrue(p >= 0.0, $"probability {p} was negative");
            Assert.IsTrue(p <= 1.0, $"probability {p} exceeded 1");
        }
        Assert.AreEqual(1.0, probs.Sum(), 1e-9); // softmax normalizes to Σ = 1
    }

    [TestMethod]
    public void Confidence_ZeroInput_EqualsMaxUniformProbability()
    {
        // For x = 0 the distribution is uniform 1/K, so its maximum (the confidence) is 1/K.
        // K = 5 => confidence = 0.2. Also equals the max of PredictProbabilities by definition.
        var model = new OnlineSoftmax(inputDim: 2, numClasses: 5);
        var x = Vector<double>.Build.Dense(2, 0.0);

        var confidence = model.Confidence(x);

        Assert.AreEqual(0.2, confidence, 1e-9);                       // 1/5
        Assert.AreEqual(model.PredictProbabilities(x).Max(), confidence, 1e-9);
    }

    [TestMethod]
    public void Predict_ReturnsArgmaxOfProbabilities()
    {
        // Predict returns the argmax class index; it must agree with the argmax of the
        // probability vector for the same input (both derive from the same softmax).
        var model = new OnlineSoftmax(inputDim: 4, numClasses: 3);
        var x = Vector<double>.Build.Dense(new[] { 2.0, -1.0, 0.5, 3.0 });

        var predicted = model.Predict(x);
        var probs = model.PredictProbabilities(x);

        var expectedArgmax = Array.IndexOf(probs, probs.Max());
        Assert.IsTrue(predicted >= 0 && predicted < 3, "class index out of range");
        Assert.AreEqual(expectedArgmax, predicted); // integer index => exact match
    }

    [TestMethod]
    public void Predict_IsDeterministic_SameSeededInitProducesIdenticalProbabilities()
    {
        // Reset() uses a fixed RNG seed (new Random(42)), so two independently-constructed
        // models with identical hyperparameters must produce bit-identical predictions.
        var modelA = new OnlineSoftmax(inputDim: 4, numClasses: 3, learningRate: 0.05, lambda: 0.002);
        var modelB = new OnlineSoftmax(inputDim: 4, numClasses: 3, learningRate: 0.05, lambda: 0.002);
        var x = Vector<double>.Build.Dense(new[] { 0.3, -1.1, 2.4, 0.9 });

        var probsA = modelA.PredictProbabilities(x);
        var probsB = modelB.PredictProbabilities(x);

        Assert.AreEqual(modelA.Predict(x), modelB.Predict(x));       // same argmax class
        Assert.AreEqual(probsA.Length, probsB.Length);
        for (int c = 0; c < probsA.Length; c++)
            Assert.AreEqual(probsA[c], probsB[c], 1e-12);            // identical init => identical output
    }

    [TestMethod]
    public void Update_ThenReset_RestoresUniformOutputAndZeroSampleCount()
    {
        // Training moves the weights; SampleCount tracks how many samples were seen.
        // Reset must return the model to the deterministic fresh state: SampleCount == 0
        // and (because b is re-zeroed) x = 0 again maps to the uniform distribution 1/K.
        var model = new OnlineSoftmax(inputDim: 3, numClasses: 2);
        var trainX = Vector<double>.Build.Dense(new[] { 1.0, 2.0, -1.0 });
        model.Update(trainX, label: 1);
        Assert.AreEqual(1, model.SampleCount); // one sample consumed

        model.Reset();

        Assert.AreEqual(0, model.SampleCount);
        var zero = Vector<double>.Build.Dense(3, 0.0);
        var probs = model.PredictProbabilities(zero);
        Assert.AreEqual(2, probs.Length);
        Assert.AreEqual(0.5, probs[0], 1e-9); // 1/2
        Assert.AreEqual(0.5, probs[1], 1e-9); // 1/2
    }

    [TestMethod]
    public void Update_IncrementsSampleCount()
    {
        // Each Update consumes exactly one sample => SampleCount increments by one per call.
        var model = new OnlineSoftmax(inputDim: 2, numClasses: 3);
        var x = Vector<double>.Build.Dense(new[] { 0.5, -0.5 });

        model.Update(x, label: 0);
        model.Update(x, label: 2);
        model.Update(x, label: 1);

        Assert.AreEqual(3, model.SampleCount); // three Update calls
    }

    [TestMethod]
    public void Update_MovesProbabilityMassTowardTrueLabel()
    {
        // One SGD step on (x, label) reduces cross-entropy for that (x, label), so the
        // probability assigned to the true label must increase after a single update.
        // Use a distinctive non-zero x so the gradient is non-trivial.
        var model = new OnlineSoftmax(inputDim: 3, numClasses: 3, learningRate: 0.5);
        var x = Vector<double>.Build.Dense(new[] { 1.0, 2.0, 3.0 });
        const int label = 2;

        var before = model.PredictProbabilities(x)[label];
        model.Update(x, label);
        var after = model.PredictProbabilities(x)[label];

        Assert.IsTrue(after > before,
            $"expected p(label) to increase after an SGD step, got before={before}, after={after}");
    }

    [TestMethod]
    public void Constructor_InvalidArguments_Throw()
    {
        // Guards from the constructor: inputDim > 0, numClasses > 1, learningRate > 0, lambda >= 0.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OnlineSoftmax(inputDim: 0, numClasses: 3));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OnlineSoftmax(inputDim: 3, numClasses: 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OnlineSoftmax(inputDim: 3, numClasses: 3, learningRate: 0.0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OnlineSoftmax(inputDim: 3, numClasses: 3, lambda: -0.1));
    }

    [TestMethod]
    public void Predict_WrongInputDimension_ThrowsArgumentException()
    {
        // Predict validates x.Count == InputDimension.
        var model = new OnlineSoftmax(inputDim: 4, numClasses: 3);
        var wrong = Vector<double>.Build.Dense(2, 1.0); // 2 != 4

        Assert.ThrowsExactly<ArgumentException>(() => model.Predict(wrong));
    }

    [TestMethod]
    public void Update_LabelOutOfRange_ThrowsArgumentException()
    {
        // Update validates 0 <= label < NumClasses.
        var model = new OnlineSoftmax(inputDim: 2, numClasses: 3);
        var x = Vector<double>.Build.Dense(new[] { 1.0, 1.0 });

        Assert.ThrowsExactly<ArgumentException>(() => model.Update(x, label: 3));  // == NumClasses
        Assert.ThrowsExactly<ArgumentException>(() => model.Update(x, label: -1)); // negative
    }
}
