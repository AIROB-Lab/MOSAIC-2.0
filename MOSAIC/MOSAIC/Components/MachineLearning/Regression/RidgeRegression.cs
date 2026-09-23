using System;
using MOSAIC.Components.MachineLearning.Interfaces;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.MachineLearning.Regression
{
    /// <summary>
    /// Online Ridge Regression using Sherman-Morrison rank-1 updates.
    /// 
    /// Mathematical formulation:
    /// - Model: ŷ = Wᵀx where W = Ainv * B
    /// - Update rule (Sherman-Morrison):
    ///   Ainv ← Ainv - (Ainv·x·xᵀ·Ainv) / (1 + xᵀ·Ainv·x)
    ///   B ← B + x·yᵀ
    /// - Initial state: Ainv = (1/λ)·I, B = 0
    /// </summary>
    public sealed class RidgeRegression : IRegressor, IStateful<RidgeState>
    {
        private readonly object _lock = new();
        private readonly double _lambda;
        private Matrix _W;
        private Matrix _Ainv;
        private Matrix _B;

        /// <inheritdoc />
        public int InputDimension { get; }

        /// <inheritdoc />
        public int OutputDimension { get; }

        /// <summary>
        /// The regularization parameter (λ).
        /// Higher values impose stronger regularization.
        /// </summary>
        public double Lambda => _lambda;

        /// <summary>
        /// Creates a new Online Ridge Regression model.
        /// </summary>
        /// <param name="inputDim">Number of input features.</param>
        /// <param name="outputDim">Number of output dimensions.</param>
        /// <param name="lambda">Regularization parameter (default: 1.0).</param>
        public RidgeRegression(int inputDim, int outputDim, double lambda = 1.0)
        {
            if (inputDim <= 0) throw new ArgumentOutOfRangeException(nameof(inputDim));
            if (outputDim <= 0) throw new ArgumentOutOfRangeException(nameof(outputDim));
            if (lambda <= 0) throw new ArgumentOutOfRangeException(nameof(lambda));

            InputDimension = inputDim;
            OutputDimension = outputDim;
            _lambda = lambda;

            _W = Matrix.Build.Dense(inputDim, outputDim);
            _Ainv = Matrix.Build.Dense(inputDim, inputDim);
            _B = Matrix.Build.Dense(inputDim, outputDim);

            Reset();
        }

        /// <inheritdoc />
        public void Reset()
        {
            lock (_lock)
            {
                _Ainv = (1.0 / _lambda) * Matrix.Build.DenseIdentity(InputDimension);
                _B = Matrix.Build.Dense(InputDimension, OutputDimension);
                _W = Matrix.Build.Dense(InputDimension, OutputDimension);
            }
        }

        /// <inheritdoc />
        public void Update(Vector x, Vector y)
        {
            if (x.Count != InputDimension)
                throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));
            if (y.Count != OutputDimension)
                throw new ArgumentException($"Expected output dimension {OutputDimension}, got {y.Count}", nameof(y));

            var xCol = x.ToColumnMatrix();
            var yCol = y.ToColumnMatrix();

            lock (_lock)
            {
                // Sherman-Morrison update
                var xAinv = _Ainv * xCol;
                var denominator = 1.0 + (xCol.Transpose() * xAinv).At(0, 0);
                _Ainv -= (1.0 / denominator) * (xAinv * xCol.Transpose() * _Ainv);

                _B += xCol * yCol.Transpose();

                _W = _Ainv * _B;
            }
        }

        /// <inheritdoc />
        public Vector Predict(Vector x)
        {
            if (x.Count != InputDimension)
                throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

            lock (_lock)
            {
                return _W.Transpose() * x;
            }
        }

        /// <inheritdoc />
        public double Confidence(Vector x)
        {
            if (x.Count != InputDimension)
                throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

            lock (_lock)
            {
                // Confidence = xᵀ·Ainv·x
                return (x.ToRowMatrix() * _Ainv * x.ToColumnMatrix()).At(0, 0);
            }
        }

        /// <inheritdoc />
        public RidgeState GetState()
        {
            lock (_lock)
            {
                return new RidgeState
                {
                    Ainv = _Ainv.Clone(),
                    B = _B.Clone()
                };
            }
        }

        /// <inheritdoc />
        public void SetState(RidgeState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            if (state.Ainv.RowCount != InputDimension || state.Ainv.ColumnCount != InputDimension)
                throw new ArgumentException($"Ainv must be {InputDimension}x{InputDimension}");
            if (state.B.RowCount != InputDimension || state.B.ColumnCount != OutputDimension)
                throw new ArgumentException($"B must be {InputDimension}x{OutputDimension}");

            lock (_lock)
            {
                _Ainv = state.Ainv.Clone();
                _B = state.B.Clone();
                _W = _Ainv * _B;
            }
        }
    }
}
