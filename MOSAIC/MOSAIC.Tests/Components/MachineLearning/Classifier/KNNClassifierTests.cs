using System.Collections.Generic;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.MachineLearning.Classifier;

namespace MOSAIC.Tests.Components.MachineLearning.Classifier;

/// <summary>
/// Known-answer tests for <see cref="KnnClassifier"/>: a brute-force k-nearest-neighbors
/// classifier. Points are stored verbatim (TrainBatch replaces, Update appends); prediction
/// takes the k = min(K, storedCount) closest samples by Euclidean (L2) distance and votes.
///
/// Voting rule:
///   weightByDistance = false  -> each neighbor contributes weight 1.
///   weightByDistance = true   -> each neighbor contributes weight 1 / (distance + 1e-10).
///   probabilities = votes / sum(votes);  Predict = argmax(probabilities) with the FIRST
///   maximal class winning ties (strict '>' scan);  Confidence = max(probabilities).
///
/// Every expected value below is derived by hand from that definition, not paraphrased from
/// the C#. Delta is 1e-9 for exact-arithmetic (unweighted) results and 1e-6 where the 1e-10
/// distance epsilon perturbs a weighted result.
/// </summary>
[TestClass]
public class KNNClassifierTests
{
    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    // ---------------------------------------------------------------- Untrained model

    [TestMethod]
    public void FreshModel_HasNoSamplesAndIsNotTrained()
    {
        // Constructor stores no samples => IsTrained is false and SampleCount is 0.
        var knn = new KnnClassifier(inputDim: 2, numClasses: 3);

        Assert.IsFalse(knn.IsTrained);
        Assert.AreEqual(0, knn.SampleCount);
    }

    [TestMethod]
    public void Predict_UntrainedModel_ReturnsZero()
    {
        // With zero stored samples Predict short-circuits to class 0.
        var knn = new KnnClassifier(inputDim: 2, numClasses: 3);

        var label = knn.Predict(Vec(1.0, 2.0));

        Assert.AreEqual(0, label);
    }

    [TestMethod]
    public void PredictProbabilities_UntrainedModel_ReturnsUniformOverClasses()
    {
        // No samples => uniform distribution of length numClasses: [1/3, 1/3, 1/3].
        var knn = new KnnClassifier(inputDim: 2, numClasses: 3);

        var probs = knn.PredictProbabilities(Vec(1.0, 2.0));

        Assert.AreEqual(3, probs.Length);
        Assert.AreEqual(1.0 / 3.0, probs[0], 1e-9);
        Assert.AreEqual(1.0 / 3.0, probs[1], 1e-9);
        Assert.AreEqual(1.0 / 3.0, probs[2], 1e-9);
    }

    // ---------------------------------------------------------------- Constructor guard

    [TestMethod]
    public void Constructor_NonPositiveK_ClampedToOne()
    {
        // K = Math.Max(1, k): a requested k of 0 becomes 1.
        var knn = new KnnClassifier(inputDim: 2, numClasses: 2, k: 0);

        Assert.AreEqual(1, knn.K);
    }

    // ---------------------------------------------------------------- Two-cluster separation

    [TestMethod]
    public void Predict_TwoSeparatedClusters_ReturnsNearestClusterLabel()
    {
        // Cluster 0 sits around the origin, cluster 1 around (10,10). With k=3 the three
        // nearest neighbours of a query hugging one cluster are ALL from that cluster, so the
        // vote is unanimous (probabilities [1,0] or [0,1]) regardless of distance weighting.
        var knn = new KnnClassifier(inputDim: 2, numClasses: 2, k: 3);
        var features = new List<Vector<double>>
        {
            Vec(0.0, 0.0), Vec(1.0, 0.0), Vec(0.0, 1.0),      // cluster 0
            Vec(10.0, 10.0), Vec(11.0, 10.0), Vec(10.0, 11.0) // cluster 1
        };
        var labels = new List<int> { 0, 0, 0, 1, 1, 1 };
        knn.TrainBatch(features, labels);

        // (0.2,0.2): 3 nearest are the cluster-0 points (~0.28 / ~0.82 / ~0.82) vs ~13.9 to cluster 1.
        var nearA = knn.Predict(Vec(0.2, 0.2));
        var probsA = knn.PredictProbabilities(Vec(0.2, 0.2));
        // (10.2,10.2): symmetric case, 3 nearest are all cluster 1.
        var nearB = knn.Predict(Vec(10.2, 10.2));

        Assert.AreEqual(0, nearA);
        Assert.AreEqual(1, nearB);
        Assert.AreEqual(1.0, probsA[0], 1e-6); // unanimous cluster-0 vote => all mass on class 0
        Assert.AreEqual(0.0, probsA[1], 1e-6);
    }

    // ---------------------------------------------------------------- Unweighted voting (exact)

    [TestMethod]
    public void PredictProbabilities_UnweightedMajority_MatchesVoteFractions()
    {
        // weightByDistance=false, k=3, query=(0,0). Stored (dist to query):
        //   (0,0) L0 -> 0,  (1,0) L0 -> 1,  (5,0) L1 -> 5. All 3 are the top-3.
        // Equal weights => votes = [2, 1], total 3 => probs = [2/3, 1/3].
        var knn = new KnnClassifier(inputDim: 2, numClasses: 2, k: 3, weightByDistance: false);
        knn.TrainBatch(
            new List<Vector<double>> { Vec(0.0, 0.0), Vec(1.0, 0.0), Vec(5.0, 0.0) },
            new List<int> { 0, 0, 1 });

        var probs = knn.PredictProbabilities(Vec(0.0, 0.0));
        var label = knn.Predict(Vec(0.0, 0.0));
        var conf = knn.Confidence(Vec(0.0, 0.0));

        Assert.AreEqual(2, probs.Length);
        Assert.AreEqual(2.0 / 3.0, probs[0], 1e-9);
        Assert.AreEqual(1.0 / 3.0, probs[1], 1e-9);
        Assert.AreEqual(0, label);            // argmax => class 0
        Assert.AreEqual(2.0 / 3.0, conf, 1e-9); // Confidence = max prob
    }

    // ---------------------------------------------------------------- Weighted voting (1/dist)

    [TestMethod]
    public void PredictProbabilities_InverseDistanceWeighted_MatchesHandComputedValue()
    {
        // weightByDistance=true, k=2, query=(0,0). Stored (dist):
        //   (1,0) L0 -> 1,  (2,0) L1 -> 2.
        // weights = 1/(dist+1e-10): w0 ~= 1/1 = 1, w1 ~= 1/2 = 0.5.
        // votes = [1, 0.5], total 1.5 => probs = [2/3, 1/3] (the 1e-10 epsilon shifts this
        // by ~1e-10, hence delta 1e-6).
        var knn = new KnnClassifier(inputDim: 2, numClasses: 2, k: 2, weightByDistance: true);
        knn.TrainBatch(
            new List<Vector<double>> { Vec(1.0, 0.0), Vec(2.0, 0.0) },
            new List<int> { 0, 1 });

        var probs = knn.PredictProbabilities(Vec(0.0, 0.0));
        var label = knn.Predict(Vec(0.0, 0.0));

        Assert.AreEqual(2, probs.Length);
        Assert.AreEqual(2.0 / 3.0, probs[0], 1e-6);
        Assert.AreEqual(1.0 / 3.0, probs[1], 1e-6);
        Assert.AreEqual(0, label); // closer neighbour (class 0) dominates
    }

    [TestMethod]
    public void PredictProbabilities_AnyTrainedModel_SumsToOne()
    {
        // Invariant: probabilities always normalise to 1 across all classes.
        var knn = new KnnClassifier(inputDim: 2, numClasses: 3, k: 3, weightByDistance: true);
        knn.TrainBatch(
            new List<Vector<double>> { Vec(0.0, 0.0), Vec(1.0, 1.0), Vec(5.0, 5.0), Vec(6.0, 6.0) },
            new List<int> { 0, 1, 2, 2 });

        var probs = knn.PredictProbabilities(Vec(2.0, 2.0));

        double sum = 0.0;
        foreach (var p in probs) sum += p;
        Assert.AreEqual(1.0, sum, 1e-9);
    }

    // ---------------------------------------------------------------- Tie handling

    [TestMethod]
    public void Predict_TiedUnweightedVotes_ReturnsLowestClassIndex()
    {
        // weightByDistance=false, k=2, query=(0,0). Stored (dist):
        //   (1,0) L0 -> 1,  (0,1) L1 -> 1. Equal weights => votes = [1, 1], probs = [0.5, 0.5].
        // argmax uses a strict '>' scan, so the FIRST maximal class (0) wins the tie.
        var knn = new KnnClassifier(inputDim: 2, numClasses: 2, k: 2, weightByDistance: false);
        knn.TrainBatch(
            new List<Vector<double>> { Vec(1.0, 0.0), Vec(0.0, 1.0) },
            new List<int> { 0, 1 });

        var probs = knn.PredictProbabilities(Vec(0.0, 0.0));
        var label = knn.Predict(Vec(0.0, 0.0));
        var conf = knn.Confidence(Vec(0.0, 0.0));

        Assert.AreEqual(0.5, probs[0], 1e-9);
        Assert.AreEqual(0.5, probs[1], 1e-9);
        Assert.AreEqual(0, label);          // tie broken toward the lower index
        Assert.AreEqual(0.5, conf, 1e-9);
    }

    // ---------------------------------------------------------------- Incremental Update + eviction

    [TestMethod]
    public void Update_BeyondMaxSamples_EvictsOldestAndBoundsCount()
    {
        // maxSamples=2. Third Update pushes count to 3 > 2, so the oldest sample (index 0,
        // the (0,0) point) is evicted, leaving exactly 2 stored samples.
        var knn = new KnnClassifier(inputDim: 2, numClasses: 2, maxSamples: 2);

        knn.Update(Vec(0.0, 0.0), 0);
        knn.Update(Vec(1.0, 1.0), 0);
        knn.Update(Vec(10.0, 10.0), 1);

        Assert.AreEqual(2, knn.SampleCount);
        Assert.IsTrue(knn.IsTrained);
    }

    [TestMethod]
    public void Update_NewHigherLabel_GrowsClassCount()
    {
        // numClasses starts at 2; updating with label 4 grows it to max(2, 4+1) = 5.
        var knn = new KnnClassifier(inputDim: 2, numClasses: 2);

        knn.Update(Vec(0.0, 0.0), 4);

        Assert.AreEqual(5, knn.NumClasses);
        Assert.AreEqual(5, knn.OutputDimension); // OutputDimension mirrors NumClasses
    }

    // ---------------------------------------------------------------- TrainBatch behavior/guards

    [TestMethod]
    public void TrainBatch_ReplacesSamplesAndExpandsClassCount()
    {
        // Constructed with numClasses=1; labels contain 2 => numClasses grows to max(1, 2+1)=3.
        var knn = new KnnClassifier(inputDim: 2, numClasses: 1);
        knn.TrainBatch(
            new List<Vector<double>> { Vec(0.0, 0.0), Vec(1.0, 0.0), Vec(2.0, 0.0) },
            new List<int> { 0, 1, 2 });

        Assert.AreEqual(3, knn.SampleCount);
        Assert.AreEqual(3, knn.NumClasses);

        // A second call CLEARS the previous set before loading (2 samples => count 2, not 5).
        knn.TrainBatch(
            new List<Vector<double>> { Vec(9.0, 9.0), Vec(8.0, 8.0) },
            new List<int> { 0, 1 });

        Assert.AreEqual(2, knn.SampleCount);
        Assert.AreEqual(3, knn.NumClasses); // never shrinks: max(3, 1+1) = 3
    }

    [TestMethod]
    public void TrainBatch_EmptyInput_Throws()
    {
        // features.Count == 0 violates the non-empty precondition.
        var knn = new KnnClassifier(inputDim: 2, numClasses: 2);

        Assert.ThrowsExactly<System.ArgumentException>(() =>
            knn.TrainBatch(new List<Vector<double>>(), new List<int>()));
    }

    [TestMethod]
    public void TrainBatch_MismatchedLengths_Throws()
    {
        // features.Count (2) != labels.Count (1) violates the equal-length precondition.
        var knn = new KnnClassifier(inputDim: 2, numClasses: 2);

        Assert.ThrowsExactly<System.ArgumentException>(() =>
            knn.TrainBatch(
                new List<Vector<double>> { Vec(0.0, 0.0), Vec(1.0, 0.0) },
                new List<int> { 0 }));
    }

    // ---------------------------------------------------------------- Reset

    [TestMethod]
    public void Reset_AfterTraining_ClearsStoredSamples()
    {
        // Reset empties the sample/label stores => back to the untrained state.
        var knn = new KnnClassifier(inputDim: 2, numClasses: 2, k: 1);
        knn.TrainBatch(
            new List<Vector<double>> { Vec(0.0, 0.0), Vec(5.0, 5.0) },
            new List<int> { 0, 1 });

        knn.Reset();

        Assert.IsFalse(knn.IsTrained);
        Assert.AreEqual(0, knn.SampleCount);
        Assert.AreEqual(0, knn.Predict(Vec(0.0, 0.0))); // untrained Predict short-circuits to 0
    }
}
