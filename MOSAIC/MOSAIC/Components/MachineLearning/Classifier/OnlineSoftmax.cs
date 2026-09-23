using System;
using MOSAIC.Components.MachineLearning.Interfaces;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.MachineLearning.Classification;

/// <summary>
/// Online Softmax Classifier using Stochastic Gradient Descent.
/// 
/// Mathematical formulation:
/// - Model: p(y=c|x) = softmax(W·x + b)
/// - Update rule (SGD with L2 regularization):
///   W ← W - lr * (gradient + λ·W)
///   b ← b - lr * gradient
///   where gradient = softmax(W·x + b) - one_hot(label)
/// </summary>
public sealed class OnlineSoftmax : IClassifier, IStateful<SoftmaxState>
{
    private readonly object _lock = new();
    private readonly double _learningRate;
    private readonly double _lambda;
    private Matrix _W;      // Weight matrix: NumClasses x InputDim
    private Vector _b;      // Bias vector: NumClasses
    private int _sampleCount;

    /// <inheritdoc />
    public int InputDimension { get; }

    /// <inheritdoc />
    public int OutputDimension => NumClasses;

    /// <inheritdoc />
    public int NumClasses { get; }

    /// <summary>
    /// Number of samples the model has been trained on.
    /// </summary>
    public int SampleCount => _sampleCount;

    /// <summary>
    /// The learning rate for SGD updates.
    /// </summary>
    public double LearningRate => _learningRate;

    /// <summary>
    /// The L2 regularization parameter (λ).
    /// </summary>
    public double Lambda => _lambda;

    /// <summary>
    /// Creates a new Online Softmax Classifier.
    /// </summary>
    /// <param name="inputDim">Number of input features.</param>
    /// <param name="numClasses">Number of classes.</param>
    /// <param name="learningRate">Learning rate for SGD (default: 0.01).</param>
    /// <param name="lambda">L2 regularization parameter (default: 0.001).</param>
    public OnlineSoftmax(int inputDim, int numClasses, double learningRate = 0.01, double lambda = 0.001)
    {
        if (inputDim <= 0) throw new ArgumentOutOfRangeException(nameof(inputDim));
        if (numClasses <= 1) throw new ArgumentOutOfRangeException(nameof(numClasses), "Need at least 2 classes");
        if (learningRate <= 0) throw new ArgumentOutOfRangeException(nameof(learningRate));
        if (lambda < 0) throw new ArgumentOutOfRangeException(nameof(lambda));

        InputDimension = inputDim;
        NumClasses = numClasses;
        _learningRate = learningRate;
        _lambda = lambda;

        _W = Matrix.Build.Dense(numClasses, inputDim);
        _b = Vector.Build.Dense(numClasses);
        _sampleCount = 0;

        Reset();
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_lock)
        {
            // Initialize weights with small random values (Xavier initialization)
            var rng = new Random(42);
            var scale = Math.Sqrt(2.0 / (InputDimension + NumClasses));
            _W = Matrix.Build.Dense(NumClasses, InputDimension, (i, j) => (rng.NextDouble() - 0.5) * 2 * scale);
            _b = Vector.Build.Dense(NumClasses, 0.0);
            _sampleCount = 0;
        }
    }

    /// <inheritdoc />
    public void Update(Vector x, int label)
    {
        if (x.Count != InputDimension)
            throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));
        if (label < 0 || label >= NumClasses)
            throw new ArgumentException($"Label {label} out of range [0, {NumClasses})", nameof(label));

        lock (_lock)
        {
            var probs = ComputeSoftmax(x);

            // Compute gradient: prob - one_hot(label)
            var gradient = probs.Clone();
            gradient[label] -= 1.0;

            for (int c = 0; c < NumClasses; c++)
            {
                var g = gradient[c];
                for (int d = 0; d < InputDimension; d++)
                {
                    _W[c, d] -= _learningRate * (g * x[d] + _lambda * _W[c, d]);
                }
                _b[c] -= _learningRate * g;
            }

            _sampleCount++;
        }
    }

    /// <inheritdoc />
    public int Predict(Vector x)
    {
        if (x.Count != InputDimension)
            throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

        lock (_lock)
        {
            var probs = ComputeSoftmax(x);
            return probs.MaximumIndex();
        }
    }

    /// <inheritdoc />
    public double[] PredictProbabilities(Vector x)
    {
        if (x.Count != InputDimension)
            throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

        lock (_lock)
        {
            return ComputeSoftmax(x).ToArray();
        }
    }

    /// <inheritdoc />
    public double Confidence(Vector x)
    {
        if (x.Count != InputDimension)
            throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

        lock (_lock)
        {
            var probs = ComputeSoftmax(x);
            return probs.Maximum();
        }
    }

    /// <summary>
    /// Computes softmax probabilities with numerical stability.
    /// </summary>
    private Vector ComputeSoftmax(Vector x)
    {
        var logits = _W * x + _b;

        // Softmax with numerical stability (subtract max)
        var maxLogit = logits.Maximum();
        var expLogits = logits.Map(l => Math.Exp(l - maxLogit));
        var sum = expLogits.Sum();

        return expLogits / sum;
    }

    /// <inheritdoc />
    public SoftmaxState GetState()
    {
        lock (_lock)
        {
            return new SoftmaxState
            {
                W = _W.Clone(),
                B = _b.Clone(),
                SampleCount = _sampleCount
            };
        }
    }

    /// <inheritdoc />
    public void SetState(SoftmaxState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.W.RowCount != NumClasses || state.W.ColumnCount != InputDimension)
            throw new ArgumentException($"W must be {NumClasses}x{InputDimension}");
        if (state.B.Count != NumClasses)
            throw new ArgumentException($"B must have {NumClasses} elements");

        lock (_lock)
        {
            _W = state.W.Clone();
            _b = state.B.Clone();
            _sampleCount = state.SampleCount;
        }
    }
}
