using System;
using System.Collections.Generic;
using System.Linq;
using MOSAIC.Components.MachineLearning.Interfaces;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.Components.MachineLearning.Classifier;

/// <summary>
/// k-Nearest Neighbors classifier with brute-force distance search.
/// </summary>
/// <remarks>
/// <para>
/// Stores all training samples in memory and classifies new points by majority vote
/// among the <see cref="K"/> closest neighbors (Euclidean distance).
/// </para>
/// <para>
/// <b>Batch training:</b> Call <see cref="TrainBatch"/> with the full labelled dataset,
/// or use <see cref="Update"/> to add samples incrementally. No model fitting is required —
/// kNN is a lazy learner that stores data and queries at prediction time.
/// </para>
/// <para>
/// <b>Distance weighting:</b> When <see cref="WeightByDistance"/> is <see langword="true"/>,
/// each neighbor's vote is weighted by <c>1 / (distance + ε)</c>. Otherwise, all neighbors
/// vote equally.
/// </para>
/// <para>
/// <b>Max samples:</b> When <see cref="MaxSamples"/> is exceeded, the oldest samples are
/// evicted to bound memory usage. Set to 0 for unlimited.
/// </para>
/// </remarks>
public sealed class KnnClassifier : IClassifier
{
    #region Fields

    private readonly int _inputDim;
    private int _numClasses;
    private readonly List<Vector> _samples = new();
    private readonly List<int> _labels = new();

    #endregion

    #region Properties

    /// <inheritdoc />
    public int InputDimension => _inputDim;

    /// <inheritdoc />
    public int OutputDimension => _numClasses;

    /// <inheritdoc />
    public int NumClasses => _numClasses;

    /// <summary>Number of neighbors to consider. Default: 5.</summary>
    public int K { get; set; }

    /// <summary>Weight votes by inverse distance. Default: true.</summary>
    public bool WeightByDistance { get; set; }

    /// <summary>Maximum stored samples (0 = unlimited). Default: 10000.</summary>
    public int MaxSamples { get; set; }

    /// <summary><see langword="true"/> when at least one sample has been stored.</summary>
    public bool IsTrained => _samples.Count > 0;

    /// <summary>Number of stored training samples.</summary>
    public int SampleCount => _samples.Count;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new <see cref="KnnClassifier"/>.
    /// </summary>
    /// <param name="inputDim">Feature vector dimensionality.</param>
    /// <param name="numClasses">Number of classes.</param>
    /// <param name="k">Number of neighbors. Default: 5.</param>
    /// <param name="weightByDistance">Weight votes by inverse distance. Default: true.</param>
    /// <param name="maxSamples">Maximum stored samples (0 = unlimited). Default: 10000.</param>
    public KnnClassifier(int inputDim, int numClasses, int k = 5,
                         bool weightByDistance = true, int maxSamples = 10000)
    {
        _inputDim = inputDim;
        _numClasses = numClasses;
        K = Math.Max(1, k);
        WeightByDistance = weightByDistance;
        MaxSamples = maxSamples;
    }

    #endregion

    #region Batch Training

    /// <summary>
    /// Replaces the stored sample set with a full labelled dataset.
    /// </summary>
    public void TrainBatch(IReadOnlyList<Vector> features, IReadOnlyList<int> labels)
    {
        if (features.Count == 0 || features.Count != labels.Count)
            throw new ArgumentException("Features and labels must be non-empty and equal length.");

        _samples.Clear();
        _labels.Clear();

        _numClasses = Math.Max(_numClasses, labels.Max() + 1);

        for (int i = 0; i < features.Count; i++)
        {
            _samples.Add(features[i].Clone());
            _labels.Add(labels[i]);
        }

        Console.WriteLine($"[KnnClassifier] Loaded {_samples.Count} samples, {_numClasses} classes, k={K}");
    }

    #endregion

    #region IClassifier

    /// <inheritdoc />
    public void Update(Vector x, int label)
    {
        _numClasses = Math.Max(_numClasses, label + 1);
        _samples.Add(x.Clone());
        _labels.Add(label);

        if (MaxSamples > 0 && _samples.Count > MaxSamples)
        {
            _samples.RemoveAt(0);
            _labels.RemoveAt(0);
        }
    }

    /// <inheritdoc />
    public int Predict(Vector x)
    {
        if (_samples.Count == 0) return 0;

        var probs = PredictProbabilities(x);
        int best = 0;
        double bestP = probs[0];
        for (int c = 1; c < probs.Length; c++)
        {
            if (probs[c] > bestP) { bestP = probs[c]; best = c; }
        }
        return best;
    }

    /// <inheritdoc />
    public double[] PredictProbabilities(Vector x)
    {
        if (_samples.Count == 0)
            return Enumerable.Repeat(1.0 / _numClasses, _numClasses).ToArray();

        int k = Math.Min(K, _samples.Count);

        var distances = new (double Dist, int Label)[_samples.Count];
        for (int i = 0; i < _samples.Count; i++)
            distances[i] = ((x - _samples[i]).L2Norm(), _labels[i]);

        // Full sort — the k nearest are then the first k entries.
        Array.Sort(distances, (a, b) => a.Dist.CompareTo(b.Dist));

        var votes = new double[_numClasses];

        for (int i = 0; i < k; i++)
        {
            var (dist, label) = distances[i];
            double weight = WeightByDistance ? 1.0 / (dist + 1e-10) : 1.0;
            votes[label] += weight;
        }

        double total = votes.Sum();
        if (total < 1e-15)
            return Enumerable.Repeat(1.0 / _numClasses, _numClasses).ToArray();

        return votes.Select(v => v / total).ToArray();
    }

    /// <inheritdoc />
    public double Confidence(Vector x) => PredictProbabilities(x).Max();

    /// <inheritdoc />
    public void Reset()
    {
        _samples.Clear();
        _labels.Clear();
    }

    #endregion
}