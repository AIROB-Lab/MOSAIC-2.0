using System;
using MathNet.Numerics.Distributions;
using MOSAIC.Components.MachineLearning.Interfaces;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.MachineLearning.Regression
{
    /// <summary>
    /// Random Fourier Features (RFF) wrapper for regression.
    /// 
    /// Transforms inputs via φ(x) = cos(Ω·x + β) before passing to inner regressor.
    /// This approximates an RBF kernel, allowing linear methods to capture nonlinear patterns.
    /// 
    /// As the feature dimension D increases, this approaches kernel ridge regression
    /// with an RBF kernel of bandwidth σ.
    /// </summary>
    public sealed class RFFRegression : IRegressor, IStateful<RidgeState>
    {
        private readonly IRegressor _inner;
        private readonly Matrix _Omega;
        private readonly Vector _beta;
        private readonly int _seed;

        /// <inheritdoc />
        public int InputDimension { get; }

        /// <inheritdoc />
        public int OutputDimension => _inner.OutputDimension;

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
        /// Creates a new RFF Regressor.
        /// </summary>
        /// <param name="inputDim">Original input dimension.</param>
        /// <param name="outputDim">Output dimension.</param>
        /// <param name="lambda">Regularization parameter for inner Ridge regression.</param>
        /// <param name="sigma">RBF kernel bandwidth (standard deviation for Ω sampling).</param>
        /// <param name="featureDim">Number of random Fourier features (D).</param>
        /// <param name="seed">Random seed for reproducibility (null = random).</param>
        public RFFRegression(
            int inputDim,
            int outputDim,
            double lambda = 1.0,
            double sigma = 1.0,
            int featureDim = 300,
            int? seed = null)
        {
            if (inputDim <= 0) throw new ArgumentOutOfRangeException(nameof(inputDim));
            if (outputDim <= 0) throw new ArgumentOutOfRangeException(nameof(outputDim));
            if (sigma <= 0) throw new ArgumentOutOfRangeException(nameof(sigma));
            if (featureDim <= 0) throw new ArgumentOutOfRangeException(nameof(featureDim));

            InputDimension = inputDim;
            FeatureDimension = featureDim;
            _seed = seed ?? Random.Shared.Next();

            var rng = new Random(_seed);
            // Ω ~ N(0, σ) : D × d matrix
            _Omega = Matrix.Build.Random(featureDim, inputDim, new Normal(0.0, sigma, rng));
            // β ~ U(-π, π) : D-dimensional vector
            _beta = Vector.Build.Random(featureDim, new ContinuousUniform(-Math.PI, Math.PI, rng));

            // Inner regressor works in feature space
            _inner = new RidgeRegression(featureDim, outputDim, lambda);
        }

        /// <summary>
        /// Creates a new RFF Regressor wrapping an existing regressor.
        /// </summary>
        /// <param name="inputDim">Original input dimension.</param>
        /// <param name="inner">The inner regressor (must have InputDimension == featureDim).</param>
        /// <param name="sigma">RBF kernel bandwidth.</param>
        /// <param name="featureDim">Number of random features.</param>
        /// <param name="seed">Random seed.</param>
        public RFFRegression(
            int inputDim,
            IRegressor inner,
            double sigma = 1.0,
            int featureDim = 300,
            int? seed = null)
        {
            if (inputDim <= 0) throw new ArgumentOutOfRangeException(nameof(inputDim));
            ArgumentNullException.ThrowIfNull(inner);
            if (inner.InputDimension != featureDim)
                throw new ArgumentException($"Inner regressor input dim ({inner.InputDimension}) must match featureDim ({featureDim})");

            InputDimension = inputDim;
            FeatureDimension = featureDim;
            _seed = seed ?? Random.Shared.Next();
            _inner = inner;

            var rng = new Random(_seed);
            _Omega = Matrix.Build.Random(featureDim, inputDim, new Normal(0.0, sigma, rng));
            _beta = Vector.Build.Random(featureDim, new ContinuousUniform(-Math.PI, Math.PI, rng));
        }

        /// <summary>
        /// Transforms input to RFF space: φ(x) = cos(Ω·x + β)
        /// </summary>
        private Vector Phi(Vector x) => (_Omega * x + _beta).PointwiseCos();

        /// <inheritdoc />
        public void Update(Vector x, Vector y)
        {
            if (x.Count != InputDimension)
                throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

            _inner.Update(Phi(x), y);
        }

        /// <inheritdoc />
        public Vector Predict(Vector x)
        {
            if (x.Count != InputDimension)
                throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

            return _inner.Predict(Phi(x));
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
        public RidgeState GetState()
        {
            if (_inner is IStateful<RidgeState> stateful)
                return stateful.GetState();

            throw new NotSupportedException("Inner regressor does not support state serialization");
        }

        /// <inheritdoc />
        public void SetState(RidgeState state)
        {
            if (_inner is IStateful<RidgeState> stateful)
            {
                stateful.SetState(state);
                return;
            }

            throw new NotSupportedException("Inner regressor does not support state serialization");
        }
    }
}
