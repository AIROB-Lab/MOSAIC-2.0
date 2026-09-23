using System;
using System.Collections.Generic;
using System.Linq;
using MOSAIC.Components.MachineLearning.Interfaces;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.MachineLearning.Classification;

/// <summary>
/// Simple threshold-based classifier using per-class mean templates and distance.
/// </summary>
/// <remarks>
/// <para>
/// Computes the centroid (mean feature vector) for each class from training data,
/// then classifies new points by nearest centroid. An optional <see cref="Threshold"/>
/// rejects low-confidence predictions — returning <see cref="RejectClass"/> (default: 0 = rest)
/// when the maximum class probability falls below the threshold.
/// </para>
/// <para>
/// This is the simplest possible classifier and serves as a useful baseline.
/// It is equivalent to a 1-nearest-centroid classifier.
/// </para>
/// </remarks>
public sealed class ThresholdClassifier : IClassifier
{
    #region Fields

    private readonly int _inputDim;
    private int _numClasses;
    private Vector[]? _centroids;
    private int[]? _classCounts;

    #endregion

    #region Properties

    /// <inheritdoc />
    public int InputDimension => _inputDim;

    /// <inheritdoc />
    public int OutputDimension => _numClasses;

    /// <inheritdoc />
    public int NumClasses => _numClasses;

    /// <summary>
    /// Minimum confidence (max probability) required for a valid prediction.
    /// Below this threshold, <see cref="RejectClass"/> is returned. Default: 0.0 (no rejection).
    /// </summary>
    public double Threshold { get; set; }

    /// <summary>
    /// Class label returned when confidence is below <see cref="Threshold"/>.
    /// Default: 0 (typically "rest").
    /// </summary>
    public int RejectClass { get; set; }

    /// <summary><see langword="true"/> once <see cref="TrainBatch"/> has been called.</summary>
    public bool IsTrained => _centroids != null;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new <see cref="ThresholdClassifier"/>.
    /// </summary>
    /// <param name="inputDim">Feature vector dimensionality.</param>
    /// <param name="numClasses">Number of classes.</param>
    /// <param name="threshold">Rejection threshold (0 = no rejection). Default: 0.</param>
    /// <param name="rejectClass">Class label for rejected predictions. Default: 0.</param>
    public ThresholdClassifier(int inputDim, int numClasses,
                                double threshold = 0.0, int rejectClass = 0)
    {
        _inputDim = inputDim;
        _numClasses = numClasses;
        Threshold = threshold;
        RejectClass = rejectClass;
    }

    #endregion

    #region Batch Training

    /// <summary>
    /// Computes per-class centroids from a labelled dataset.
    /// </summary>
    public void TrainBatch(IReadOnlyList<Vector> features, IReadOnlyList<int> labels)
    {
        if (features.Count == 0 || features.Count != labels.Count)
            throw new ArgumentException("Features and labels must be non-empty and equal length.");

        _numClasses = Math.Max(_numClasses, labels.Max() + 1);
        _centroids = new Vector[_numClasses];
        _classCounts = new int[_numClasses];

        for (int c = 0; c < _numClasses; c++)
            _centroids[c] = MathNet.Numerics.LinearAlgebra.Double.DenseVector.Create(_inputDim, 0.0);

        for (int i = 0; i < features.Count; i++)
        {
            _centroids[labels[i]] += features[i];
            _classCounts[labels[i]]++;
        }

        for (int c = 0; c < _numClasses; c++)
        {
            if (_classCounts[c] > 0)
                _centroids[c] /= _classCounts[c];
        }

        Console.WriteLine($"[ThresholdClassifier] Trained: {features.Count} samples, {_numClasses} classes");
    }

    #endregion

    #region IClassifier

    /// <inheritdoc />
    public void Update(Vector x, int label)
    {
        _numClasses = Math.Max(_numClasses, label + 1);

        if (_centroids == null || _centroids.Length < _numClasses)
        {
            var newCentroids = new Vector[_numClasses];
            var newCounts = new int[_numClasses];

            for (int c = 0; c < _numClasses; c++)
                newCentroids[c] = MathNet.Numerics.LinearAlgebra.Double.DenseVector.Create(_inputDim, 0.0);

            if (_centroids != null)
            {
                for (int c = 0; c < _centroids.Length; c++)
                {
                    newCentroids[c] = _centroids[c];
                    newCounts[c] = _classCounts![c];
                }
            }

            _centroids = newCentroids;
            _classCounts = newCounts;
        }

        // Incremental mean update
        _classCounts![label]++;
        int n = _classCounts[label];
        _centroids[label] += (x - _centroids[label]) / n;
    }

    /// <inheritdoc />
    public int Predict(Vector x)
    {
        var probs = PredictProbabilities(x);
        double maxProb = probs.Max();

        if (maxProb < Threshold)
            return RejectClass;

        return Array.IndexOf(probs, maxProb);
    }

    /// <inheritdoc />
    public double[] PredictProbabilities(Vector x)
    {
        if (_centroids == null)
            return Enumerable.Repeat(1.0 / _numClasses, _numClasses).ToArray();

        var rawDists = new double[_numClasses];
        for (int c = 0; c < _numClasses; c++)
            rawDists[c] = (x - _centroids[c]).L2Norm();

        // Adaptive temperature: scale by median distance
        var sorted = rawDists.OrderBy(d => d).ToArray();
        double median = sorted[sorted.Length / 2];
        double temperature = Math.Max(median, 1e-10);

        var scores = new double[_numClasses];
        for (int c = 0; c < _numClasses; c++)
            scores[c] = -rawDists[c] / temperature;

        double max = scores.Max();
        var exps = scores.Select(s => Math.Exp(s - max)).ToArray();
        double sum = exps.Sum();
        return exps.Select(e => e / sum).ToArray();
    }

    /// <inheritdoc />
    public double Confidence(Vector x) => PredictProbabilities(x).Max();

    /// <inheritdoc />
    public void Reset()
    {
        _centroids = null;
        _classCounts = null;
    }

    #endregion
}