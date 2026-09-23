using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.MachineLearning.Classifier;

namespace MOSAIC.Tests.Components.MachineLearning.Classifier;

/// <summary>
/// Known-answer and invariant tests for <see cref="RandomForestClassifier"/>: a batch random
/// forest (bagging + Gini-split decision trees, majority vote).
///
/// The split-criterion helpers (<c>GiniImpurity</c>/<c>WeightedGini</c>) are private instance
/// methods (they close over the internal class count), so they are exercised only through the
/// public surface. The randomised parts (bootstrap sampling, random feature subsets) are pinned
/// with the constructor <c>seed</c> and asserted via robust invariants:
/// <list type="bullet">
///   <item><description>Untrained model has exact, hand-derivable behaviour (Predict = 0, uniform probabilities).</description></item>
///   <item><description>On linearly separable clusters every tree can split perfectly, so the ensemble
///   classifies all training points correctly regardless of the RNG draw.</description></item>
///   <item><description>Probabilities are a valid distribution (each in [0,1], sum = 1); Confidence = max probability.</description></item>
///   <item><description>Same seed + same data + same order => identical predictions (determinism).</description></item>
/// </list>
/// </summary>
[TestClass]
public class RandomForestClassifierTests
{
    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    /// <summary>
    /// Two linearly separable 2-D clusters: class 0 near the origin (both features in {0,1}),
    /// class 1 near (10,10) (both features in {10,11}). A single threshold on <em>either</em>
    /// feature (e.g. 5.5) separates them perfectly, so any decision tree — whichever single
    /// feature it happens to pick (√2 = 1 feature per split) — classifies the training set exactly.
    /// </summary>
    private static (List<Vector<double>> X, List<int> Y) SeparableTwoClass()
    {
        var x = new List<Vector<double>>
        {
            Vec(0, 0), Vec(1, 0), Vec(0, 1), Vec(1, 1),       // class 0
            Vec(10, 10), Vec(11, 10), Vec(10, 11), Vec(11, 11) // class 1
        };
        var y = new List<int> { 0, 0, 0, 0, 1, 1, 1, 1 };
        return (x, y);
    }

    // ---------------------------------------------------------------- Constructor / hyperparameters

    [TestMethod]
    public void Constructor_StoresHyperparametersAndDimensions()
    {
        // Every argument is echoed by the corresponding property; OutputDimension mirrors NumClasses.
        var forest = new RandomForestClassifier(inputDim: 5, numClasses: 3, numTrees: 42,
                                                maxDepth: 7, minSamplesLeaf: 4, seed: 1);

        Assert.AreEqual(5, forest.InputDimension);
        Assert.AreEqual(3, forest.NumClasses);
        Assert.AreEqual(3, forest.OutputDimension); // OutputDimension == NumClasses
        Assert.AreEqual(42, forest.NumTrees);
        Assert.AreEqual(7, forest.MaxDepth);
        Assert.AreEqual(4, forest.MinSamplesLeaf);
        Assert.IsFalse(forest.IsTrained); // no TrainBatch yet
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-3)]
    public void Constructor_NonPositiveMinSamplesLeaf_ClampsToOne(int requested)
    {
        // MinSamplesLeaf = Math.Max(1, minSamplesLeaf) => 0 or negative collapses to 1.
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 2, minSamplesLeaf: requested);

        Assert.AreEqual(1, forest.MinSamplesLeaf);
    }

    // ---------------------------------------------------------------- Untrained behaviour (exact)

    [TestMethod]
    public void Predict_UntrainedModel_ReturnsZero()
    {
        // With no trees built, Predict short-circuits to class 0 for any input.
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 3, seed: 0);

        Assert.AreEqual(0, forest.Predict(Vec(7.0, -4.0)));
    }

    [TestMethod]
    public void PredictProbabilities_UntrainedModel_ReturnsUniformDistribution()
    {
        // No trees => Enumerable.Repeat(1/numClasses, numClasses). numClasses = 3 => [1/3,1/3,1/3].
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 3, seed: 0);

        double[] probs = forest.PredictProbabilities(Vec(1.0, 2.0));

        Assert.AreEqual(3, probs.Length);
        Assert.AreEqual(1.0 / 3.0, probs[0], 1e-9);
        Assert.AreEqual(1.0 / 3.0, probs[1], 1e-9);
        Assert.AreEqual(1.0 / 3.0, probs[2], 1e-9);
        Assert.AreEqual(1.0, probs.Sum(), 1e-9); // valid distribution
    }

    [TestMethod]
    public void Confidence_UntrainedModel_EqualsReciprocalOfClassCount()
    {
        // Confidence = max(uniform probabilities) = 1/numClasses = 1/4.
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 4, seed: 0);

        Assert.AreEqual(1.0 / 4.0, forest.Confidence(Vec(0.0, 0.0)), 1e-9);
    }

    // ---------------------------------------------------------------- TrainBatch (seeded)

    [TestMethod]
    public void TrainBatch_AfterTraining_SetsIsTrained()
    {
        // IsTrained flips false -> true once the tree ensemble is built.
        var (x, y) = SeparableTwoClass();
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 2, numTrees: 10, seed: 42);
        Assert.IsFalse(forest.IsTrained);

        forest.TrainBatch(x, y);

        Assert.IsTrue(forest.IsTrained);
    }

    [TestMethod]
    public void TrainBatch_SeparableData_ClassifiesAllTrainingPointsCorrectly()
    {
        // Perfectly separable clusters => every tree splits them exactly (or is a pure-class leaf),
        // so the majority vote reproduces each training label. Deterministic under the fixed seed.
        var (x, y) = SeparableTwoClass();
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 2, numTrees: 21, seed: 42);

        forest.TrainBatch(x, y);

        for (int i = 0; i < x.Count; i++)
            Assert.AreEqual(y[i], forest.Predict(x[i]), $"training point {i} misclassified");
    }

    [TestMethod]
    public void Predict_UnseenPointsInsideClusters_TakeClusterLabel()
    {
        // A point deep inside cluster 0 => class 0; deep inside cluster 1 => class 1.
        var (x, y) = SeparableTwoClass();
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 2, numTrees: 21, seed: 42);
        forest.TrainBatch(x, y);

        Assert.AreEqual(0, forest.Predict(Vec(0.5, 0.5)));
        Assert.AreEqual(1, forest.Predict(Vec(10.5, 10.5)));
    }

    [TestMethod]
    public void PredictProbabilities_TrainedModel_IsValidDistributionAndConfidenceIsMax()
    {
        // Probabilities are vote fractions: each in [0,1], summing to exactly 1 (one vote per tree).
        // Confidence is defined as the maximum probability. On a clearly class-0 point the majority
        // (hence the max) sits on class 0, so confidence > 0.5 for a correct 2-class majority.
        var (x, y) = SeparableTwoClass();
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 2, numTrees: 21, seed: 42);
        forest.TrainBatch(x, y);
        var point = Vec(0.5, 0.5);

        double[] probs = forest.PredictProbabilities(point);
        double confidence = forest.Confidence(point);

        Assert.AreEqual(2, probs.Length);
        Assert.IsTrue(probs.All(p => p >= 0.0 && p <= 1.0), "probabilities out of [0,1]");
        Assert.AreEqual(1.0, probs.Sum(), 1e-9);        // sums to one
        Assert.AreEqual(probs.Max(), confidence, 1e-9); // Confidence == max probability
        Assert.IsTrue(confidence > 0.5, "correct 2-class majority must exceed 0.5");
        Assert.AreEqual(0, forest.Predict(point));      // argmax is class 0
    }

    [TestMethod]
    public void TrainBatch_SameSeedSameData_ProducesIdenticalPredictions()
    {
        // The forest is fully determined by its seeded RNG, so two independently trained instances
        // agree on every prediction and probability vector.
        var (x, y) = SeparableTwoClass();
        var a = new RandomForestClassifier(inputDim: 2, numClasses: 2, numTrees: 17, seed: 7);
        var b = new RandomForestClassifier(inputDim: 2, numClasses: 2, numTrees: 17, seed: 7);
        a.TrainBatch(x, y);
        b.TrainBatch(x, y);

        foreach (var probe in new[] { Vec(0.5, 0.5), Vec(10.5, 10.5), Vec(5.0, 5.0), Vec(2.0, 9.0) })
        {
            Assert.AreEqual(a.Predict(probe), b.Predict(probe));
            var pa = a.PredictProbabilities(probe);
            var pb = b.PredictProbabilities(probe);
            for (int c = 0; c < pa.Length; c++)
                Assert.AreEqual(pa[c], pb[c], 1e-12);
        }
    }

    [TestMethod]
    public void TrainBatch_LabelsExceedInitialClassCount_GrowsClassCount()
    {
        // Constructed with numClasses = 2, but the data carries a label 2, so
        // _numClasses = max(2, labels.Max()+1) = 3 after training (and OutputDimension follows).
        var x = new List<Vector<double>>
        {
            Vec(0, 0), Vec(1, 1),   // class 0
            Vec(10, 10), Vec(11, 11), // class 1
            Vec(20, 20), Vec(21, 21)  // class 2
        };
        var y = new List<int> { 0, 0, 1, 1, 2, 2 };
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 2, numTrees: 15, seed: 3);

        forest.TrainBatch(x, y);

        Assert.AreEqual(3, forest.NumClasses);
        Assert.AreEqual(3, forest.OutputDimension);
        Assert.AreEqual(3, forest.PredictProbabilities(Vec(20.5, 20.5)).Length); // one slot per class
    }

    [TestMethod]
    public void TrainBatch_EmptyData_Throws()
    {
        // Guard: features.Count == 0 => ArgumentException.
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 2, seed: 0);

        Assert.ThrowsExactly<System.ArgumentException>(
            () => forest.TrainBatch(new List<Vector<double>>(), new List<int>()));
    }

    [TestMethod]
    public void TrainBatch_MismatchedFeatureAndLabelCounts_Throws()
    {
        // Guard: features.Count != labels.Count => ArgumentException.
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 2, seed: 0);
        var x = new List<Vector<double>> { Vec(0, 0), Vec(1, 1) };
        var y = new List<int> { 0 }; // one label short

        Assert.ThrowsExactly<System.ArgumentException>(() => forest.TrainBatch(x, y));
    }

    // ---------------------------------------------------------------- Update (incremental retrain)

    [TestMethod]
    public void Update_ReachesRetrainInterval_TrainsAndClassifies()
    {
        // Update buffers samples and retrains when buffer.Count % RetrainInterval == 0.
        // With RetrainInterval = 8 and the 8 separable samples, the 8th Update triggers TrainBatch;
        // the resulting forest classifies both clusters correctly.
        var (x, y) = SeparableTwoClass();
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 2, numTrees: 15, seed: 42)
        {
            RetrainInterval = 8
        };

        for (int i = 0; i < x.Count; i++)
            forest.Update(x[i], y[i]);

        Assert.IsTrue(forest.IsTrained);
        Assert.AreEqual(0, forest.Predict(Vec(0.5, 0.5)));
        Assert.AreEqual(1, forest.Predict(Vec(10.5, 10.5)));
    }

    [TestMethod]
    public void Update_BeforeRetrainInterval_RemainsUntrained()
    {
        // Only 7 of the required 8 samples buffered => no retrain fires => still untrained,
        // so Predict falls back to class 0.
        var (x, y) = SeparableTwoClass();
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 2, numTrees: 15, seed: 42)
        {
            RetrainInterval = 8
        };

        for (int i = 0; i < 7; i++)
            forest.Update(x[i], y[i]);

        Assert.IsFalse(forest.IsTrained);
        Assert.AreEqual(0, forest.Predict(Vec(10.5, 10.5))); // untrained fallback, not the cluster-1 label
    }

    // ---------------------------------------------------------------- Reset

    [TestMethod]
    public void Reset_AfterTraining_RestoresUntrainedBehaviour()
    {
        // Reset clears the trees (and buffer): IsTrained goes false, Predict returns 0, and
        // probabilities revert to the uniform prior.
        var (x, y) = SeparableTwoClass();
        var forest = new RandomForestClassifier(inputDim: 2, numClasses: 2, numTrees: 15, seed: 42);
        forest.TrainBatch(x, y);
        Assert.IsTrue(forest.IsTrained);

        forest.Reset();

        Assert.IsFalse(forest.IsTrained);
        Assert.AreEqual(0, forest.Predict(Vec(10.5, 10.5)));
        double[] probs = forest.PredictProbabilities(Vec(10.5, 10.5));
        Assert.AreEqual(0.5, probs[0], 1e-9); // uniform over 2 classes again
        Assert.AreEqual(0.5, probs[1], 1e-9);
    }
}
