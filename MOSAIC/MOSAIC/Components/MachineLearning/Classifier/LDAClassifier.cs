using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using MOSAIC.Components.MachineLearning.Interfaces;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.MachineLearning.Classification;

/// <summary>
/// Batch Fisher Linear Discriminant Analysis classifier.
/// </summary>
/// <remarks>
/// <para>
/// Computes the optimal linear projection that maximises the ratio of between-class
/// scatter to within-class scatter, then classifies new points by nearest class centroid
/// in the projected space.
/// </para>
/// <para>
/// <b>Training:</b> Call <see cref="TrainBatch"/> with the full labelled dataset.
/// The classifier computes per-class means, the pooled within-class scatter matrix
/// <c>Sw</c>, and the between-class scatter matrix <c>Sb</c>, then solves the generalised
/// eigenvalue problem <c>Sw⁻¹ · Sb</c> for the top <c>min(C−1, d)</c> discriminant directions.
/// </para>
/// <para>
/// <b>Prediction:</b> Projects the input onto the discriminant axes and returns the
/// class whose projected centroid is nearest (Euclidean distance).
/// </para>
/// <para>
/// <b>Online <see cref="Update"/>:</b> Appends to an internal buffer and retriggers
/// <see cref="TrainBatch"/> every <see cref="RetrainInterval"/> samples to keep the
/// model current without requiring an external retrain call.
/// </para>
/// </remarks>
public sealed class LdaClassifier : IClassifier
{
    #region Fields

    private readonly int _inputDim;
    private int _numClasses;
    private long _sampleCount;

    /// <summary>Projection matrix W [d × k] where k = min(C-1, d).</summary>
    private Matrix? _W;

    /// <summary>Projected class centroids [C × k].</summary>
    private Vector[]? _projectedCentroids;

    /// <summary>Internal buffer for incremental retraining.</summary>
    private readonly List<(Vector X, int Label)> _buffer = new();

    /// <summary>Regularization added to Sw diagonal for numerical stability.</summary>
    private readonly double _regularization;

    #endregion

    #region Properties

    /// <inheritdoc />
    public int InputDimension => _inputDim;

    /// <inheritdoc />
    public int OutputDimension => _numClasses;

    /// <inheritdoc />
    public int NumClasses => _numClasses;

    /// <summary>
    /// Number of samples between automatic retrains during <see cref="Update"/> calls.
    /// Set to 0 to disable auto-retrain (manual <see cref="TrainBatch"/> only).
    /// </summary>
    public int RetrainInterval { get; set; } = 50;

    /// <summary><see langword="true"/> once <see cref="TrainBatch"/> has been called successfully.</summary>
    public bool IsTrained => _W != null;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new <see cref="LdaClassifier"/>.
    /// </summary>
    /// <param name="inputDim">Feature vector dimensionality.</param>
    /// <param name="numClasses">Number of classes (can grow via <see cref="TrainBatch"/>).</param>
    /// <param name="regularization">Tikhonov regularization added to Sw diagonal. Default: 1e-6.</param>
    public LdaClassifier(int inputDim, int numClasses, double regularization = 1e-6)
    {
        _inputDim = inputDim;
        _numClasses = numClasses;
        _regularization = regularization;
    }

    #endregion

    #region Batch Training

    /// <summary>
    /// Trains the LDA classifier on a complete labelled dataset.
    /// </summary>
    /// <param name="features">List of feature vectors.</param>
    /// <param name="labels">Corresponding class labels (0-based).</param>
    /// <exception cref="ArgumentException">Thrown when inputs are empty or mismatched.</exception>
    public void TrainBatch(IReadOnlyList<Vector> features, IReadOnlyList<int> labels)
    {
        if (features.Count == 0 || features.Count != labels.Count)
            throw new ArgumentException("Features and labels must be non-empty and equal length.");

        int n = features.Count;
        int d = _inputDim;
        int C = labels.Max() + 1;
        _numClasses = Math.Max(_numClasses, C);

        var perClass = new List<Vector>[_numClasses];
        for (int c = 0; c < _numClasses; c++)
            perClass[c] = new List<Vector>();

        for (int i = 0; i < n; i++)
            perClass[labels[i]].Add(features[i]);

        Vector globalMean = DenseVector.Create(d, 0.0);
        foreach (var x in features)
            globalMean = globalMean + x;
        globalMean = globalMean / n;

        var classMeans = new Vector[_numClasses];
        for (int c = 0; c < _numClasses; c++)
        {
            if (perClass[c].Count == 0)
            {
                classMeans[c] = globalMean.Clone();
                continue;
            }
            classMeans[c] = DenseVector.Create(d, 0.0);
            foreach (var x in perClass[c])
                classMeans[c] = classMeans[c] + x;
            classMeans[c] = classMeans[c] / perClass[c].Count;
        }

        // Within-class scatter Sw = Σ_c Σ_i (x_i - μ_c)(x_i - μ_c)^T
        Matrix Sw = DenseMatrix.Create(d, d, 0.0);
        for (int c = 0; c < _numClasses; c++)
        {
            foreach (var x in perClass[c])
            {
                var diff = x - classMeans[c];
                Sw = Sw + diff.OuterProduct(diff);
            }
        }

        for (int i = 0; i < d; i++)
            Sw[i, i] += _regularization;

        // Between-class scatter Sb = Σ_c n_c (μ_c - μ)(μ_c - μ)^T
        Matrix Sb = DenseMatrix.Create(d, d, 0.0);
        for (int c = 0; c < _numClasses; c++)
        {
            if (perClass[c].Count == 0) continue;
            var diff = classMeans[c] - globalMean;
            Sb = Sb + perClass[c].Count * diff.OuterProduct(diff);
        }

        var SwInv = Sw.Inverse();
        var M = SwInv * Sb;
        var evd = M.Evd();

        // Take top k = min(C-1, d) eigenvectors, ranked by descending real part of the eigenvalue
        int k = Math.Min(_numClasses - 1, d);
        k = Math.Max(k, 1);

        var eigenPairs = new List<(double Value, Vector Vec)>();
        for (int i = 0; i < evd.EigenValues.Count; i++)
        {
            var ev = evd.EigenValues[i];
            eigenPairs.Add((ev.Real, evd.EigenVectors.Column(i)));
        }
        eigenPairs.Sort((a, b) => b.Value.CompareTo(a.Value));

        _W = DenseMatrix.Create(d, k, 0.0);
        for (int j = 0; j < k; j++)
            _W.SetColumn(j, eigenPairs[j].Vec);

        _projectedCentroids = new Vector[_numClasses];
        for (int c = 0; c < _numClasses; c++)
            _projectedCentroids[c] = _W.TransposeThisAndMultiply(classMeans[c]);

        _sampleCount = n;
        Console.WriteLine($"[LdaClassifier] Trained: {n} samples, {_numClasses} classes, {k} discriminant axes");
    }

    #endregion

    #region IClassifier

    /// <inheritdoc />
    public void Update(Vector x, int label)
    {
        _buffer.Add((x.Clone(), label));
        _sampleCount++;

        if (RetrainInterval > 0 && _buffer.Count % RetrainInterval == 0)
        {
            var allX = _buffer.Select(b => b.X).ToList();
            var allY = _buffer.Select(b => b.Label).ToList();
            TrainBatch(allX, allY);
        }
    }

    /// <inheritdoc />
    public int Predict(Vector x)
    {
        if (_W == null || _projectedCentroids == null)
            return 0;

        var projected = _W.TransposeThisAndMultiply(x);

        int bestClass = 0;
        double bestDist = double.MaxValue;
        for (int c = 0; c < _numClasses; c++)
        {
            var dist = (projected - _projectedCentroids[c]).L2Norm();
            if (dist < bestDist)
            {
                bestDist = dist;
                bestClass = c;
            }
        }
        return bestClass;
    }

    /// <inheritdoc />
    public double[] PredictProbabilities(Vector x)
    {
        if (_W == null || _projectedCentroids == null)
            return Enumerable.Repeat(1.0 / _numClasses, _numClasses).ToArray();

        var projected = _W.TransposeThisAndMultiply(x);

        var rawDists = new double[_numClasses];
        for (int c = 0; c < _numClasses; c++)
            rawDists[c] = (projected - _projectedCentroids[c]).L2Norm();

        // Adaptive temperature: scale by median distance so softmax has meaningful spread
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
    public double Confidence(Vector x)
    {
        var probs = PredictProbabilities(x);
        return probs.Max();
    }

    /// <inheritdoc />
    public void Reset()
    {
        _W = null;
        _projectedCentroids = null;
        _buffer.Clear();
        _sampleCount = 0;
    }

    #endregion
}