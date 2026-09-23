using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.MachineLearning.Classification;

namespace MOSAIC.Tests.Components.MachineLearning.Classifier;

/// <summary>
/// Known-answer / invariant tests for <see cref="LdaClassifier"/>: batch Fisher LDA that projects
/// onto the top k = min(C-1, d) discriminant axes and classifies by nearest projected centroid.
///
/// The eigendecomposition itself carries no RNG, so once the synthetic training set is generated
/// from a seeded <see cref="Random"/> the whole pipeline is deterministic. Tests therefore assert
/// hard values where the arithmetic is closed-form (untrained guards, uniform priors) and robust
/// invariants for the trained model (correct label on well-separated clusters, probabilities that
/// sum to 1 and whose argmax agrees with <see cref="LdaClassifier.Predict"/>).
/// </summary>
[TestClass]
public class LDAClassifierTests
{
    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    /// <summary>
    /// Builds two well-separated 2-D clusters: class 0 centred on (0,0), class 1 on (5,5),
    /// each coordinate jittered by a seeded uniform draw in [-0.5, 0.5]. Interleaves the labels
    /// so both classes are present throughout.
    /// </summary>
    private static (List<Vector<double>> X, List<int> Y) MakeTwoClusters(int perClass, int seed)
    {
        var rng = new Random(seed);
        double Jitter() => rng.NextDouble() - 0.5; // uniform in [-0.5, 0.5]

        var xs = new List<Vector<double>>();
        var ys = new List<int>();
        for (int i = 0; i < perClass; i++)
        {
            xs.Add(Vec(0.0 + Jitter(), 0.0 + Jitter()));
            ys.Add(0);
            xs.Add(Vec(5.0 + Jitter(), 5.0 + Jitter()));
            ys.Add(1);
        }
        return (xs, ys);
    }

    // ---------------------------------------------------------------- construction

    [TestMethod]
    public void Constructor_FreshModel_IsUntrainedWithExpectedDimensions()
    {
        // Nothing has been trained yet: _W is null => IsTrained is false.
        // OutputDimension mirrors NumClasses; RetrainInterval defaults to 50.
        var model = new LdaClassifier(inputDim: 2, numClasses: 3);

        Assert.IsFalse(model.IsTrained);
        Assert.AreEqual(2, model.InputDimension);
        Assert.AreEqual(3, model.NumClasses);
        Assert.AreEqual(3, model.OutputDimension);
        Assert.AreEqual(50, model.RetrainInterval);
    }

    // ---------------------------------------------------------------- untrained guards

    [TestMethod]
    public void Predict_BeforeTraining_ReturnsZero()
    {
        // Guard: _W == null => Predict short-circuits to class 0 regardless of the input.
        var model = new LdaClassifier(inputDim: 2, numClasses: 3);

        int label = model.Predict(Vec(9.0, -4.0));

        Assert.AreEqual(0, label);
    }

    [TestMethod]
    public void PredictProbabilities_BeforeTraining_ReturnsUniform()
    {
        // Guard: _W == null => uniform prior, each entry = 1 / NumClasses = 1/3.
        // Confidence is the max of that distribution => also 1/3.
        var model = new LdaClassifier(inputDim: 2, numClasses: 3);
        var x = Vec(1.0, 2.0);

        double[] probs = model.PredictProbabilities(x);

        Assert.AreEqual(3, probs.Length);              // one entry per class, checked before values
        Assert.AreEqual(1.0, probs.Sum(), 1e-9);       // proper distribution
        Assert.AreEqual(1.0 / 3.0, probs[0], 1e-9);
        Assert.AreEqual(1.0 / 3.0, probs[1], 1e-9);
        Assert.AreEqual(1.0 / 3.0, probs[2], 1e-9);
        Assert.AreEqual(1.0 / 3.0, model.Confidence(x), 1e-9);
    }

    // ---------------------------------------------------------------- batch training

    [TestMethod]
    public void TrainBatch_WellSeparatedClusters_TrainsAndClassifiesCorrectly()
    {
        // Two clusters ~7 apart with jitter <= 0.5 are linearly well separated. With C=2 classes in
        // d=2 the projection dimension is k = min(C-1, d) = min(1, 2) = 1: LDA collapses the data
        // onto the single discriminant axis, and nearest-projected-centroid then labels held-out
        // points by which cluster they fall into. The result is deterministic for a fixed seed.
        var (x, y) = MakeTwoClusters(perClass: 25, seed: 42);
        var model = new LdaClassifier(inputDim: 2, numClasses: 2);

        model.TrainBatch(x, y);

        Assert.IsTrue(model.IsTrained);
        Assert.AreEqual(0, model.Predict(Vec(0.1, -0.1))); // sits on the class-0 centroid
        Assert.AreEqual(1, model.Predict(Vec(4.9, 5.1)));  // sits on the class-1 centroid
    }

    [TestMethod]
    public void PredictProbabilities_AfterTraining_SumsToOneAndAgreesWithPredict()
    {
        // Softmax over negative scaled centroid distances preserves ordering, so the largest
        // probability corresponds to the nearest centroid — exactly the class Predict returns.
        // Confidence is defined as that maximum probability.
        var (x, y) = MakeTwoClusters(perClass: 25, seed: 42);
        var model = new LdaClassifier(inputDim: 2, numClasses: 2);
        model.TrainBatch(x, y);
        var probe = Vec(0.0, 0.0); // deep inside class 0

        double[] probs = model.PredictProbabilities(probe);

        Assert.AreEqual(2, probs.Length);                       // dimensions before values
        Assert.AreEqual(1.0, probs.Sum(), 1e-9);
        int argmax = probs[0] >= probs[1] ? 0 : 1;              // ties -> 0, matching Predict's first-min rule
        Assert.AreEqual(model.Predict(probe), argmax);
        Assert.AreEqual(0, argmax);                             // nearest centroid is class 0
        Assert.AreEqual(probs.Max(), model.Confidence(probe), 1e-9);
    }

    // ---------------------------------------------------------------- input guards

    [TestMethod]
    public void TrainBatch_InvalidInputs_ThrowsArgumentException()
    {
        // Guard: features.Count == 0  OR  features.Count != labels.Count  => ArgumentException.
        var model = new LdaClassifier(inputDim: 2, numClasses: 2);

        Assert.ThrowsExactly<ArgumentException>(() =>
            model.TrainBatch(new List<Vector<double>>(), new List<int>()));                 // empty

        Assert.ThrowsExactly<ArgumentException>(() =>
            model.TrainBatch(new List<Vector<double>> { Vec(0, 0), Vec(1, 1) },
                             new List<int> { 0 }));                                          // length mismatch
    }

    // ---------------------------------------------------------------- reset

    [TestMethod]
    public void Reset_AfterTraining_ClearsTrainedState()
    {
        // Reset nulls _W and the projected centroids, so IsTrained falls back to false and
        // Predict returns to its untrained guard value of 0.
        var (x, y) = MakeTwoClusters(perClass: 25, seed: 42);
        var model = new LdaClassifier(inputDim: 2, numClasses: 2);
        model.TrainBatch(x, y);
        Assert.IsTrue(model.IsTrained);

        model.Reset();

        Assert.IsFalse(model.IsTrained);
        Assert.AreEqual(0, model.Predict(Vec(4.9, 5.1))); // guard again, despite being a class-1 point
    }

    // ---------------------------------------------------------------- online retrain threshold

    [TestMethod]
    public void Update_ReachesRetrainInterval_BecomesTrained()
    {
        // Auto-retrain fires when buffer.Count % RetrainInterval == 0. With interval 4 the model
        // stays untrained after 3 buffered samples and trains itself on the 4th.
        var model = new LdaClassifier(inputDim: 2, numClasses: 2) { RetrainInterval = 4 };

        model.Update(Vec(0.0, 0.0), 0);   // buffer.Count = 1
        model.Update(Vec(5.0, 5.0), 1);   // buffer.Count = 2
        model.Update(Vec(0.2, -0.1), 0);  // buffer.Count = 3 -> 3 % 4 != 0
        Assert.IsFalse(model.IsTrained);

        model.Update(Vec(5.1, 4.9), 1);   // buffer.Count = 4 -> 4 % 4 == 0 -> TrainBatch runs

        Assert.IsTrue(model.IsTrained);
    }
}
