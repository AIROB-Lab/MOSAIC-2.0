using System;
using MathNet.Numerics.Distributions;
using MOSAIC.Components.MachineLearning.Interfaces;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.MachineLearning.Classification;

/// <summary>
/// Random Fourier Features (RFF) wrapper for classification.
/// 
/// Transforms inputs via φ(x) = √(2/D)·cos(Ω·x + β) before passing to inner classifier.
/// This approximates an RBF kernel, allowing linear methods to capture nonlinear patterns.
/// 
/// As the feature dimension D increases, this approaches kernel classification
/// with an RBF kernel of bandwidth σ.
/// </summary>
public sealed class RFFClassification : IClassifier, IStateful<SoftmaxState>
{
    private readonly IClassifier _inner;
    private readonly Matrix _Omega;
    private readonly Vector _beta;
    private readonly int _seed;

    /// <inheritdoc />
    public int InputDimension { get; }

    /// <inheritdoc />
    public int OutputDimension => _inner.OutputDimension;

    /// <inheritdoc />
    public int NumClasses => _inner.NumClasses;

    /// <summary>
    /// The dimension of the RFF feature space.
    /// </summary>
    public int FeatureDimension { get; }

    /// <summary>
    /// The random seed used to generate Ω and β.
    /// Store this to recreate identical feature transforms.
    /// </summary>
    public int Seed => _seed;

    /// <summary>
    /// Creates a new RFF Classifier.
    /// </summary>
    /// <param name="inputDim">Original input dimension.</param>
    /// <param name="numClasses">Number of classes.</param>
    /// <param name="learningRate">Learning rate for inner classifier.</param>
    /// <param name="lambda">Regularization parameter for inner classifier.</param>
    /// <param name="sigma">RBF kernel bandwidth; Ω is sampled with standard deviation 1/σ.</param>
    /// <param name="featureDim">Number of random Fourier features (D).</param>
    /// <param name="seed">Random seed for reproducibility (null = random).</param>
    public RFFClassification(
        int inputDim,
        int numClasses,
        double learningRate = 0.01,
        double lambda = 0.001,
        double sigma = 1.0,
        int featureDim = 300,
        int? seed = null)
    {
        if (inputDim <= 0) throw new ArgumentOutOfRangeException(nameof(inputDim));
        if (numClasses <= 1) throw new ArgumentOutOfRangeException(nameof(numClasses));
        if (sigma <= 0) throw new ArgumentOutOfRangeException(nameof(sigma));
        if (featureDim <= 0) throw new ArgumentOutOfRangeException(nameof(featureDim));

        InputDimension = inputDim;
        FeatureDimension = featureDim;
        _seed = seed ?? Random.Shared.Next();

        var rng = new Random(_seed);
        // Ω ~ N(0, 1/σ) : D × d matrix
        _Omega = Matrix.Build.Random(featureDim, inputDim, new Normal(0.0, 1.0 / sigma, rng));
        // β ~ U(0, 2π) : D-dimensional vector
        _beta = Vector.Build.Random(featureDim, new ContinuousUniform(0, 2 * Math.PI, rng));

        // Inner classifier works in feature space
        _inner = new OnlineSoftmax(featureDim, numClasses, learningRate, lambda);
    }

    /// <summary>
    /// Creates a new RFF Classifier wrapping an existing classifier.
    /// </summary>
    /// <param name="inputDim">Original input dimension.</param>
    /// <param name="inner">The inner classifier (must have InputDimension == featureDim).</param>
    /// <param name="sigma">RBF kernel bandwidth.</param>
    /// <param name="featureDim">Number of random features.</param>
    /// <param name="seed">Random seed.</param>
    public RFFClassification(
        int inputDim,
        IClassifier inner,
        double sigma = 1.0,
        int featureDim = 300,
        int? seed = null)
    {
        if (inputDim <= 0) throw new ArgumentOutOfRangeException(nameof(inputDim));
        ArgumentNullException.ThrowIfNull(inner);
        if (inner.InputDimension != featureDim)
            throw new ArgumentException($"Inner classifier input dim ({inner.InputDimension}) must match featureDim ({featureDim})");

        InputDimension = inputDim;
        FeatureDimension = featureDim;
        _seed = seed ?? Random.Shared.Next();
        _inner = inner;

        var rng = new Random(_seed);
        _Omega = Matrix.Build.Random(featureDim, inputDim, new Normal(0.0, 1.0 / sigma, rng));
        _beta = Vector.Build.Random(featureDim, new ContinuousUniform(0, 2 * Math.PI, rng));
    }

    /// <summary>
    /// Transforms input to RFF space: φ(x) = sqrt(2/D) * cos(Ω·x + β)
    /// </summary>
    private Vector Phi(Vector x)
    {
        var scale = Math.Sqrt(2.0 / FeatureDimension);
        return (_Omega * x + _beta).Map(v => scale * Math.Cos(v));
    }

    /// <inheritdoc />
    public void Update(Vector x, int label)
    {
        if (x.Count != InputDimension)
            throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

        _inner.Update(Phi(x), label);
    }

    /// <inheritdoc />
    public int Predict(Vector x)
    {
        if (x.Count != InputDimension)
            throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

        return _inner.Predict(Phi(x));
    }

    /// <inheritdoc />
    public double[] PredictProbabilities(Vector x)
    {
        if (x.Count != InputDimension)
            throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

        return _inner.PredictProbabilities(Phi(x));
    }

    /// <inheritdoc />
    public double Confidence(Vector x)
    {
        if (x.Count != InputDimension)
            throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

        return _inner.Confidence(Phi(x));
    }

    /// <inheritdoc />
    public void Reset() => _inner.Reset();

    /// <inheritdoc />
    public SoftmaxState GetState()
    {
        if (_inner is IStateful<SoftmaxState> stateful)
            return stateful.GetState();

        throw new NotSupportedException("Inner classifier does not support state serialization");
    }

    /// <inheritdoc />
    public void SetState(SoftmaxState state)
    {
        if (_inner is IStateful<SoftmaxState> stateful)
        {
            stateful.SetState(state);
            return;
        }

        throw new NotSupportedException("Inner classifier does not support state serialization");
    }
}
