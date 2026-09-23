using System;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.MachineLearning.Classification;

namespace MOSAIC.Tests.Components.MachineLearning.Classifier;

/// <summary>
/// Known-answer tests for <see cref="ThresholdClassifier"/>: a nearest-centroid classifier with an
/// optional confidence-rejection threshold.
///
/// Training: each class centroid is the mean feature vector of its samples.
/// Scoring (<see cref="ThresholdClassifier.PredictProbabilities"/>) for a trained model:
///   rawDist_c   = ||x - centroid_c||₂
///   temperature = max( sorted(rawDist)[N/2], 1e-10 )        // N = NumClasses, integer-division index
///   score_c     = -rawDist_c / temperature
///   prob        = stableSoftmax(score) = exp(score_c - max) / Σ exp(score_k - max)
/// <see cref="ThresholdClassifier.Predict"/> returns argmax(prob), unless max(prob) &lt; Threshold, in
/// which case it returns <see cref="ThresholdClassifier.RejectClass"/> (the "&lt;" is strict, so a
/// probability exactly equal to the threshold is NOT rejected).
/// Untrained (no <c>TrainBatch</c>/<c>Update</c>) models return the uniform distribution 1/NumClasses.
/// All expecteds below are derived by hand from these definitions.
/// </summary>
[TestClass]
public class ThresholdClassifierTests
{
    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    /// <summary>
    /// 1-D, two-class model. Class 0 samples {0, 2} => centroid 1.0; class 1 samples {8, 12} =>
    /// centroid 10.0. Midpoint (equidistant point) is 5.5.
    /// </summary>
    private static ThresholdClassifier TrainedTwoClassModel(double threshold = 0.0, int rejectClass = 0)
    {
        var model = new ThresholdClassifier(inputDim: 1, numClasses: 2,
                                            threshold: threshold, rejectClass: rejectClass);
        var features = new[] { Vec(0.0), Vec(2.0), Vec(8.0), Vec(12.0) };
        var labels = new[] { 0, 0, 1, 1 };
        model.TrainBatch(features, labels);
        return model;
    }

    // ---------------------------------------------------------------- Untrained state

    [TestMethod]
    public void PredictProbabilities_UntrainedModel_ReturnsUniformDistribution()
    {
        // No training => _centroids is null => the method returns 1/NumClasses for every class,
        // independent of x. NumClasses = 4 => each probability is exactly 0.25.
        var model = new ThresholdClassifier(inputDim: 2, numClasses: 4);
        var x = Vec(3.0, -7.0);

        var probs = model.PredictProbabilities(x);

        Assert.AreEqual(4, probs.Length);
        Assert.AreEqual(0.25, probs[0], 1e-9); // 1/4
        Assert.AreEqual(0.25, probs[1], 1e-9); // 1/4
        Assert.AreEqual(0.25, probs[2], 1e-9); // 1/4
        Assert.AreEqual(0.25, probs[3], 1e-9); // 1/4
    }

    [TestMethod]
    public void IsTrained_FlipsFromFalseToTrueAfterTrainBatch()
    {
        // IsTrained mirrors whether _centroids has been allocated: false until TrainBatch runs.
        var model = new ThresholdClassifier(inputDim: 1, numClasses: 2);

        Assert.IsFalse(model.IsTrained);

        model.TrainBatch(new[] { Vec(0.0), Vec(10.0) }, new[] { 0, 1 });

        Assert.IsTrue(model.IsTrained);
    }

    // ---------------------------------------------------------------- Nearest-centroid Predict

    [TestMethod]
    public void Predict_PointOnClass0Side_ReturnsClass0()
    {
        // centroids [1.0, 10.0]; x = 0.  rawDist = [1, 10], nearest is class 0.
        // sorted = [1, 10], temp = sorted[1] = 10; score = [-0.1, -1.0]; softmax favours class 0.
        var model = TrainedTwoClassModel();

        var predicted = model.Predict(Vec(0.0));

        Assert.AreEqual(0, predicted);
    }

    [TestMethod]
    public void Predict_PointOnClass1Side_ReturnsClass1()
    {
        // centroids [1.0, 10.0]; x = 10.  rawDist = [9, 0], nearest is class 1.
        // sorted = [0, 9], temp = 9; score = [-1.0, 0.0]; softmax favours class 1.
        var model = TrainedTwoClassModel();

        var predicted = model.Predict(Vec(10.0));

        Assert.AreEqual(1, predicted);
    }

    [TestMethod]
    public void PredictProbabilities_TrainedModel_MatchesHandComputedSoftmax()
    {
        // centroids [1.0, 10.0]; x = 0.
        // rawDist = [|0-1|, |0-10|] = [1, 10].  sorted = [1, 10]; temperature = sorted[2/2]=sorted[1]=10.
        // score = [-1/10, -10/10] = [-0.1, -1.0].  max = -0.1.
        // exps = [exp(0), exp(-0.9)] = [1, 0.406569659740599]; sum = 1.406569659740599.
        // prob = [1/sum, 0.406569659740599/sum] = [0.7109495, 0.2890505].
        var model = TrainedTwoClassModel();

        var probs = model.PredictProbabilities(Vec(0.0));

        Assert.AreEqual(2, probs.Length);
        Assert.AreEqual(1.0, probs.Sum(), 1e-9);       // normalized distribution
        Assert.AreEqual(0.7109495, probs[0], 1e-6);    // 1 / 1.406569659740599
        Assert.AreEqual(0.2890505, probs[1], 1e-6);    // exp(-0.9) / 1.406569659740599
    }

    [TestMethod]
    public void Confidence_TrainedModel_EqualsMaxProbability()
    {
        // Confidence is defined as max(PredictProbabilities). For x = 0 the max is prob[0] = 0.7109495.
        var model = TrainedTwoClassModel();
        var x = Vec(0.0);

        var confidence = model.Confidence(x);

        Assert.AreEqual(0.7109495, confidence, 1e-6);                       // = prob[0] above
        Assert.AreEqual(model.PredictProbabilities(x).Max(), confidence, 1e-9);
    }

    // ---------------------------------------------------------------- Rejection threshold

    [TestMethod]
    public void Predict_EquidistantPoint_YieldsMaxProbabilityOneHalf()
    {
        // x = 5.5 is the midpoint of centroids 1.0 and 10.0 => rawDist = [4.5, 4.5].
        // sorted = [4.5, 4.5]; temp = 4.5; score = [-1, -1]; exps = [1, 1]; sum = 2 => prob = [0.5, 0.5].
        var model = TrainedTwoClassModel();

        var probs = model.PredictProbabilities(Vec(5.5));

        Assert.AreEqual(0.5, probs[0], 1e-9);
        Assert.AreEqual(0.5, probs[1], 1e-9);
    }

    [TestMethod]
    public void Predict_MaxProbabilityBelowThreshold_ReturnsRejectClass()
    {
        // Equidistant point => max probability is 0.5. Threshold 0.6 > 0.5 => reject.
        // RejectClass = 7 is distinct from the natural argmax (0), proving the reject branch fired.
        var model = TrainedTwoClassModel(threshold: 0.6, rejectClass: 7);

        var predicted = model.Predict(Vec(5.5));

        Assert.AreEqual(7, predicted);
    }

    [TestMethod]
    public void Predict_MaxProbabilityExactlyAtThreshold_IsNotRejected()
    {
        // max probability is exactly 0.5; threshold 0.5. The guard is "maxProb < Threshold" (strict),
        // so 0.5 < 0.5 is false => NOT rejected => returns the argmax (0), not RejectClass (7).
        var model = TrainedTwoClassModel(threshold: 0.5, rejectClass: 7);

        var predicted = model.Predict(Vec(5.5));

        Assert.AreEqual(0, predicted);
    }

    [TestMethod]
    public void Predict_ConfidentPointAboveThreshold_ReturnsPredictedClass()
    {
        // x = 10 => prob ≈ [0.2689, 0.7311], max ≈ 0.7311 >= threshold 0.6 => keep the argmax (class 1),
        // never the RejectClass.
        var model = TrainedTwoClassModel(threshold: 0.6, rejectClass: 7);

        var predicted = model.Predict(Vec(10.0));

        Assert.AreEqual(1, predicted);
    }

    // ---------------------------------------------------------------- Incremental Update

    [TestMethod]
    public void Update_IncrementalMean_ProducesSameCentroidsAsBatch()
    {
        // Update maintains a running mean per class: centroid += (x - centroid)/n.
        // Class 0: 0 -> mean 0; then 2 -> 0 + (2-0)/2 = 1.  Class 1: 8 -> 8; then 12 -> 8 + (12-8)/2 = 10.
        // Identical centroids to the batch model, so classification matches.
        var model = new ThresholdClassifier(inputDim: 1, numClasses: 2);
        model.Update(Vec(0.0), label: 0);
        model.Update(Vec(2.0), label: 0);
        model.Update(Vec(8.0), label: 1);
        model.Update(Vec(12.0), label: 1);

        Assert.IsTrue(model.IsTrained);
        Assert.AreEqual(0, model.Predict(Vec(0.0)));   // nearest centroid 1.0
        Assert.AreEqual(1, model.Predict(Vec(10.0)));  // nearest centroid 10.0
    }

    // ---------------------------------------------------------------- NumClasses growth

    [TestMethod]
    public void TrainBatch_LabelBeyondConstructorCount_GrowsNumClasses()
    {
        // Constructed with numClasses = 2, but labels contain 2 => _numClasses = max(2, 2+1) = 3.
        var model = new ThresholdClassifier(inputDim: 1, numClasses: 2);
        var features = new[] { Vec(0.0), Vec(5.0), Vec(10.0) };
        var labels = new[] { 0, 1, 2 };

        model.TrainBatch(features, labels);

        Assert.AreEqual(3, model.NumClasses);
        Assert.AreEqual(3, model.OutputDimension);            // OutputDimension == NumClasses
        Assert.AreEqual(3, model.PredictProbabilities(Vec(5.0)).Length);
    }

    // ---------------------------------------------------------------- Reset

    [TestMethod]
    public void Reset_AfterTraining_RevertsToUntrainedUniformOutput()
    {
        // Reset nulls the centroids, so IsTrained goes false and scoring falls back to uniform 1/K.
        var model = TrainedTwoClassModel();
        Assert.IsTrue(model.IsTrained);

        model.Reset();

        Assert.IsFalse(model.IsTrained);
        var probs = model.PredictProbabilities(Vec(0.0));
        Assert.AreEqual(2, probs.Length);
        Assert.AreEqual(0.5, probs[0], 1e-9); // 1/2 uniform
        Assert.AreEqual(0.5, probs[1], 1e-9); // 1/2 uniform
    }

    // ---------------------------------------------------------------- Guards

    [TestMethod]
    public void TrainBatch_EmptyDataset_ThrowsArgumentException()
    {
        // Guard: features.Count == 0 => ArgumentException.
        var model = new ThresholdClassifier(inputDim: 1, numClasses: 2);

        Assert.ThrowsExactly<ArgumentException>(
            () => model.TrainBatch(Array.Empty<Vector<double>>(), Array.Empty<int>()));
    }

    [TestMethod]
    public void TrainBatch_MismatchedLengths_ThrowsArgumentException()
    {
        // Guard: features.Count != labels.Count => ArgumentException.
        var model = new ThresholdClassifier(inputDim: 1, numClasses: 2);
        var features = new[] { Vec(0.0), Vec(1.0) }; // 2 features
        var labels = new[] { 0 };                    // 1 label

        Assert.ThrowsExactly<ArgumentException>(() => model.TrainBatch(features, labels));
    }
}
