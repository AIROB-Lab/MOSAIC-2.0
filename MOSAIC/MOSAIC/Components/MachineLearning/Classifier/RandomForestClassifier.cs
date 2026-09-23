using System;
using System.Collections.Generic;
using System.Linq;
using MOSAIC.Components.MachineLearning.Interfaces;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.Components.MachineLearning.Classifier;

/// <summary>
/// Batch Random Forest classifier with bagging and random feature subsets.
/// </summary>
/// <remarks>
/// <para>
/// Builds an ensemble of <see cref="NumTrees"/> decision trees, each trained on a
/// bootstrap sample of the data with a random subset of <c>√d</c> features at each split.
/// Classification is by majority vote across all trees.
/// </para>
/// <para>
/// <b>Split criterion:</b> Gini impurity. Each node tests all candidate feature thresholds
/// (midpoints between consecutive sorted values) and picks the split with the lowest weighted
/// Gini impurity across the two children.
/// </para>
/// <para>
/// <b>Stopping rules:</b> A node becomes a leaf when it is pure (single class), when
/// <see cref="MaxDepth"/> is reached, or when fewer than <see cref="MinSamplesLeaf"/> × 2
/// samples remain.
/// </para>
/// <para>
/// <b>Online <see cref="Update"/>:</b> Appends to an internal buffer and retriggers
/// <see cref="TrainBatch"/> every <see cref="RetrainInterval"/> samples.
/// </para>
/// </remarks>
public sealed class RandomForestClassifier : IClassifier
{
    #region Nested Types

    private sealed class DecisionTree
    {
        public int FeatureIndex;
        public double Threshold;
        public int LeafClass = -1;
        public DecisionTree? Left;
        public DecisionTree? Right;

        public bool IsLeaf => LeafClass >= 0;

        public int Predict(Vector x)
        {
            if (IsLeaf) return LeafClass;
            return x[FeatureIndex] <= Threshold
                ? Left!.Predict(x)
                : Right!.Predict(x);
        }
    }

    #endregion

    #region Fields

    private readonly int _inputDim;
    private int _numClasses;
    private readonly Random _rng;
    private DecisionTree[]? _trees;

    /// <summary>Internal buffer for incremental retraining.</summary>
    private readonly List<(Vector X, int Label)> _buffer = new();

    #endregion

    #region Properties

    /// <inheritdoc />
    public int InputDimension => _inputDim;

    /// <inheritdoc />
    public int OutputDimension => _numClasses;

    /// <inheritdoc />
    public int NumClasses => _numClasses;

    /// <summary>Number of trees in the ensemble. Default: 100.</summary>
    public int NumTrees { get; set; }

    /// <summary>Maximum tree depth. Default: 15.</summary>
    public int MaxDepth { get; set; }

    /// <summary>Minimum samples per leaf. Default: 2.</summary>
    public int MinSamplesLeaf { get; set; }

    /// <summary>Retrain interval during <see cref="Update"/> calls (0 = manual only).</summary>
    public int RetrainInterval { get; set; } = 100;

    /// <summary><see langword="true"/> once <see cref="TrainBatch"/> has completed.</summary>
    public bool IsTrained => _trees != null;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new <see cref="RandomForestClassifier"/>.
    /// </summary>
    /// <param name="inputDim">Feature vector dimensionality.</param>
    /// <param name="numClasses">Number of classes.</param>
    /// <param name="numTrees">Number of trees. Default: 100.</param>
    /// <param name="maxDepth">Maximum tree depth. Default: 15.</param>
    /// <param name="minSamplesLeaf">Minimum samples per leaf. Default: 2.</param>
    /// <param name="seed">Random seed. Null = random.</param>
    public RandomForestClassifier(int inputDim, int numClasses, int numTrees = 100,
                                   int maxDepth = 15, int minSamplesLeaf = 2, int? seed = null)
    {
        _inputDim = inputDim;
        _numClasses = numClasses;
        NumTrees = numTrees;
        MaxDepth = maxDepth;
        MinSamplesLeaf = Math.Max(1, minSamplesLeaf);
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    #endregion

    #region Batch Training

    /// <summary>
    /// Trains the forest on a complete labelled dataset.
    /// </summary>
    public void TrainBatch(IReadOnlyList<Vector> features, IReadOnlyList<int> labels)
    {
        if (features.Count == 0 || features.Count != labels.Count)
            throw new ArgumentException("Features and labels must be non-empty and equal length.");

        int n = features.Count;
        _numClasses = Math.Max(_numClasses, labels.Max() + 1);
        int featuresPerSplit = Math.Max(1, (int)Math.Sqrt(_inputDim));

        _trees = new DecisionTree[NumTrees];
        for (int t = 0; t < NumTrees; t++)
        {
            var indices = new int[n];
            for (int i = 0; i < n; i++)
                indices[i] = _rng.Next(n);

            _trees[t] = BuildTree(features, labels, indices, featuresPerSplit, 0);
        }

        Console.WriteLine($"[RandomForest] Trained: {n} samples, {_numClasses} classes, {NumTrees} trees");
    }

    #endregion

    #region Tree Building

    private DecisionTree BuildTree(IReadOnlyList<Vector> features, IReadOnlyList<int> labels,
                                    int[] indices, int featuresPerSplit, int depth)
    {
        var classCounts = new int[_numClasses];
        foreach (var idx in indices)
            classCounts[labels[idx]]++;

        int nonZeroClasses = classCounts.Count(c => c > 0);
        if (nonZeroClasses <= 1 || depth >= MaxDepth || indices.Length < MinSamplesLeaf * 2)
        {
            return new DecisionTree
            {
                LeafClass = Array.IndexOf(classCounts, classCounts.Max())
            };
        }

        var candidateFeatures = Enumerable.Range(0, _inputDim)
            .OrderBy(_ => _rng.Next())
            .Take(featuresPerSplit)
            .ToArray();

        double bestGini = double.MaxValue;
        int bestFeature = candidateFeatures[0];
        double bestThreshold = 0;
        int[]? bestLeft = null;
        int[]? bestRight = null;

        foreach (int f in candidateFeatures)
        {
            // Sorted feature values for this node (duplicates kept; equal neighbours skipped below)
            var values = indices.Select(i => features[i][f]).OrderBy(v => v).ToArray();

            // Test midpoint thresholds (skip duplicates)
            for (int i = 0; i < values.Length - 1; i++)
            {
                if (Math.Abs(values[i] - values[i + 1]) < 1e-15) continue;

                double threshold = (values[i] + values[i + 1]) * 0.5;

                var leftIdx = indices.Where(idx => features[idx][f] <= threshold).ToArray();
                var rightIdx = indices.Where(idx => features[idx][f] > threshold).ToArray();

                if (leftIdx.Length < MinSamplesLeaf || rightIdx.Length < MinSamplesLeaf)
                    continue;

                double gini = WeightedGini(labels, leftIdx, rightIdx);
                if (gini < bestGini)
                {
                    bestGini = gini;
                    bestFeature = f;
                    bestThreshold = threshold;
                    bestLeft = leftIdx;
                    bestRight = rightIdx;
                }
            }
        }

        // If no valid split found, make leaf
        if (bestLeft == null || bestRight == null)
        {
            return new DecisionTree
            {
                LeafClass = Array.IndexOf(classCounts, classCounts.Max())
            };
        }

        return new DecisionTree
        {
            FeatureIndex = bestFeature,
            Threshold = bestThreshold,
            Left = BuildTree(features, labels, bestLeft, featuresPerSplit, depth + 1),
            Right = BuildTree(features, labels, bestRight, featuresPerSplit, depth + 1)
        };
    }

    private double WeightedGini(IReadOnlyList<int> labels, int[] leftIdx, int[] rightIdx)
    {
        double n = leftIdx.Length + rightIdx.Length;
        return (leftIdx.Length / n) * GiniImpurity(labels, leftIdx)
             + (rightIdx.Length / n) * GiniImpurity(labels, rightIdx);
    }

    private double GiniImpurity(IReadOnlyList<int> labels, int[] indices)
    {
        var counts = new int[_numClasses];
        foreach (var idx in indices)
            counts[labels[idx]]++;

        double n = indices.Length;
        double gini = 1.0;
        for (int c = 0; c < _numClasses; c++)
        {
            double p = counts[c] / n;
            gini -= p * p;
        }
        return gini;
    }

    #endregion

    #region IClassifier

    /// <inheritdoc />
    public void Update(Vector x, int label)
    {
        _buffer.Add((x.Clone(), label));
        _numClasses = Math.Max(_numClasses, label + 1);

        if (RetrainInterval > 0 && _buffer.Count % RetrainInterval == 0)
        {
            TrainBatch(
                _buffer.Select(b => b.X).ToList(),
                _buffer.Select(b => b.Label).ToList());
        }
    }

    /// <inheritdoc />
    public int Predict(Vector x)
    {
        if (_trees == null) return 0;

        var votes = new int[_numClasses];
        foreach (var tree in _trees)
            votes[tree.Predict(x)]++;

        return Array.IndexOf(votes, votes.Max());
    }

    /// <inheritdoc />
    public double[] PredictProbabilities(Vector x)
    {
        if (_trees == null)
            return Enumerable.Repeat(1.0 / _numClasses, _numClasses).ToArray();

        var votes = new double[_numClasses];
        foreach (var tree in _trees)
            votes[tree.Predict(x)] += 1.0;

        double total = votes.Sum();
        return votes.Select(v => v / total).ToArray();
    }

    /// <inheritdoc />
    public double Confidence(Vector x) => PredictProbabilities(x).Max();

    /// <inheritdoc />
    public void Reset()
    {
        _trees = null;
        _buffer.Clear();
    }

    #endregion
}