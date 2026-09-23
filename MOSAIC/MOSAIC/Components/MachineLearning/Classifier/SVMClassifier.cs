using System;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using MOSAIC.Components.MachineLearning.Interfaces;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.MachineLearning.Classification;

/// <summary>
/// Multi-class linear SVM using one-vs-rest with SGD on hinge loss.
/// </summary>
/// <remarks>
/// <para>
/// Maintains one weight vector per class. For each training sample, the correct class
/// and the highest-scoring incorrect class are identified. If the margin is violated
/// (score difference &lt; 1), both weight vectors are updated with the hinge loss gradient.
/// </para>
/// <para>
/// <b>Regularization:</b> L2 weight decay is applied each step: <c>w ← (1 − η·λ) · w</c>.
/// </para>
/// <para>
/// <b>Prediction:</b> Returns the class with the highest raw score <c>w·x + b</c>.
/// Probabilities are approximated via softmax over raw scores.
/// </para>
/// </remarks>
public sealed class LinearSvmClassifier : IClassifier
{
    #region Fields

    private readonly int _inputDim;
    private int _numClasses;
    private readonly double _learningRate;
    private readonly double _lambda;
    private long _sampleCount;

    /// <summary>Weight matrix [numClasses × inputDim].</summary>
    private Matrix? _W;

    /// <summary>Bias vector [numClasses].</summary>
    private Vector? _b;

    #endregion

    #region Properties

    /// <inheritdoc />
    public int InputDimension => _inputDim;

    /// <inheritdoc />
    public int OutputDimension => _numClasses;

    /// <inheritdoc />
    public int NumClasses => _numClasses;

    /// <summary><see langword="true"/> once at least one update has been processed.</summary>
    public bool IsTrained => _W != null && _sampleCount > 0;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new <see cref="LinearSvmClassifier"/>.
    /// </summary>
    /// <param name="inputDim">Feature vector dimensionality.</param>
    /// <param name="numClasses">Number of classes.</param>
    /// <param name="learningRate">SGD learning rate. Default: 0.001.</param>
    /// <param name="lambda">L2 regularization strength. Default: 0.0001.</param>
    public LinearSvmClassifier(int inputDim, int numClasses,
                                double learningRate = 0.001, double lambda = 0.0001)
    {
        _inputDim = inputDim;
        _numClasses = numClasses;
        _learningRate = learningRate;
        _lambda = lambda;
    }

    #endregion

    #region Batch Training

    /// <summary>
    /// Trains by running multiple epochs of SGD over the full dataset.
    /// </summary>
    /// <param name="features">Feature vectors.</param>
    /// <param name="labels">Class labels (0-based).</param>
    /// <param name="epochs">Number of passes over the data. Default: 20.</param>
    public void TrainBatch(System.Collections.Generic.IReadOnlyList<Vector> features,
                           System.Collections.Generic.IReadOnlyList<int> labels,
                           int epochs = 20)
    {
        if (features.Count == 0 || features.Count != labels.Count)
            throw new ArgumentException("Features and labels must be non-empty and equal length.");

        _numClasses = Math.Max(_numClasses, labels.Max() + 1);
        EnsureInit();

        var rng = new Random(42);
        var indices = Enumerable.Range(0, features.Count).ToArray();

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            // Shuffle
            for (int i = indices.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }

            foreach (int idx in indices)
                UpdateSingle(features[idx], labels[idx]);
        }

        Console.WriteLine($"[LinearSVM] Trained: {features.Count} samples, {_numClasses} classes, {epochs} epochs");
    }

    #endregion

    #region IClassifier

    /// <inheritdoc />
    public void Update(Vector x, int label)
    {
        _numClasses = Math.Max(_numClasses, label + 1);
        EnsureInit();
        UpdateSingle(x, label);
    }

    /// <inheritdoc />
    public int Predict(Vector x)
    {
        if (_W == null || _b == null) return 0;
        var scores = ComputeScores(x);
        return scores.MaximumIndex();
    }

    /// <inheritdoc />
    public double[] PredictProbabilities(Vector x)
    {
        if (_W == null || _b == null)
            return Enumerable.Repeat(1.0 / _numClasses, _numClasses).ToArray();

        var scores = ComputeScores(x);

        // Softmax approximation
        double max = scores.Maximum();
        var exps = scores.Map(s => Math.Exp(s - max));
        double sum = exps.Sum();
        return exps.Map(e => e / sum).ToArray();
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
        _b = null;
        _sampleCount = 0;
    }

    #endregion

    #region Private Methods

    private void EnsureInit()
    {
        if (_W == null || _W.RowCount != _numClasses)
        {
            var newW = DenseMatrix.Create(_numClasses, _inputDim, 0.0);
            var newB = DenseVector.Create(_numClasses, 0.0);

            // Copy existing weights if resizing
            if (_W != null)
            {
                int copyRows = Math.Min(_W.RowCount, _numClasses);
                for (int r = 0; r < copyRows; r++)
                {
                    newW.SetRow(r, _W.Row(r));
                    newB[r] = _b![r];
                }
            }

            // Small random init for new rows
            var rng = new Random();
            for (int r = (_W?.RowCount ?? 0); r < _numClasses; r++)
                for (int c = 0; c < _inputDim; c++)
                    newW[r, c] = (rng.NextDouble() - 0.5) * 0.01;

            _W = newW;
            _b = newB;
        }
    }

    private void UpdateSingle(Vector x, int label)
    {
        _sampleCount++;
        double eta = _learningRate / (1.0 + _lambda * _sampleCount * 0.0001);

        var scores = ComputeScores(x);

        double correctScore = scores[label];
        int wrongClass = -1;
        double wrongScore = double.MinValue;
        for (int c = 0; c < _numClasses; c++)
        {
            if (c == label) continue;
            if (scores[c] > wrongScore)
            {
                wrongScore = scores[c];
                wrongClass = c;
            }
        }

        if (wrongClass < 0) return;

        // Hinge loss: max(0, 1 - (s_correct - s_wrong))
        double margin = correctScore - wrongScore;
        if (margin < 1.0)
        {
            // Update correct class: w += η * x
            for (int j = 0; j < _inputDim; j++)
                _W![label, j] += eta * x[j];
            _b![label] += eta;

            // Update wrong class: w -= η * x
            for (int j = 0; j < _inputDim; j++)
                _W![wrongClass, j] -= eta * x[j];
            _b![wrongClass] -= eta;
        }

        // L2 regularization: w *= (1 - η*λ)
        double decay = 1.0 - eta * _lambda;
        for (int c = 0; c < _numClasses; c++)
            for (int j = 0; j < _inputDim; j++)
                _W![c, j] *= decay;
    }

    private Vector ComputeScores(Vector x)
    {
        var scores = _W! * x + _b!;
        return scores;
    }

    #endregion
}