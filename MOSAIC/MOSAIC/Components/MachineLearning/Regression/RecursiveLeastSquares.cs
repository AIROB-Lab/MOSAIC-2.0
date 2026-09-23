using System;
using MOSAIC.Components.MachineLearning.Interfaces;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.MachineLearning.Regression
{
    /// <summary>
    /// Online Recursive Least Squares (RLS) regression with forgetting factor.
    ///
    /// Model:      y_hat = W^T x
    /// Gain:       k = P x / (ff + x^T P x)
    /// Weights:    W &lt;- W + k (y - W^T x)^T
    /// P update:   P &lt;- (1/ff) * (P - k x^T P)
    ///
    /// Init:       P = (1/delta) I, W = 0
    /// - ff in (0,1] is forgettingFactor (ff &lt; 1 adapts to drift)
    /// - delta > 0 is initial regularization (bigger delta => smaller initial P)
    /// </summary>
    public sealed class RecursiveLeastSquares : IRegressor, IStateful<RlsState>
    {
        private readonly object _lock = new();

        private readonly double _forgettingFactor;
        private readonly double _delta;

        private Matrix _W;   // inputDim x outputDim
        private Matrix _P;   // inputDim x inputDim

        public int InputDimension { get; }
        public int OutputDimension { get; }

        /// <summary>
        /// Forgetting factor ff in (0,1]. 1.0 = no forgetting, &lt;1 adapts to recent data.
        /// </summary>
        public double ForgettingFactor => _forgettingFactor;

        /// <summary>
        /// Initialization regularization (delta). P0 = (1/delta) I.
        /// </summary>
        public double Delta => _delta;

        public RecursiveLeastSquares(int inputDim, int outputDim, double delta = 1.0, double forgettingFactor = 1.0)
        {
            if (inputDim <= 0) throw new ArgumentOutOfRangeException(nameof(inputDim));
            if (outputDim <= 0) throw new ArgumentOutOfRangeException(nameof(outputDim));
            if (delta <= 0) throw new ArgumentOutOfRangeException(nameof(delta));
            if (forgettingFactor <= 0 || forgettingFactor > 1.0)
                throw new ArgumentOutOfRangeException(nameof(forgettingFactor), "forgettingFactor must be in (0, 1].");

            InputDimension = inputDim;
            OutputDimension = outputDim;

            _delta = delta;
            _forgettingFactor = forgettingFactor;

            _W = Matrix.Build.Dense(inputDim, outputDim);
            _P = Matrix.Build.DenseIdentity(inputDim);

            Reset();
        }

        public void Reset()
        {
            lock (_lock)
            {
                _W = Matrix.Build.Dense(InputDimension, OutputDimension);
                _P = (1.0 / _delta) * Matrix.Build.DenseIdentity(InputDimension);
            }
        }

        public void Update(Vector x, Vector y)
        {
            if (x.Count != InputDimension)
                throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));
            if (y.Count != OutputDimension)
                throw new ArgumentException($"Expected output dimension {OutputDimension}, got {y.Count}", nameof(y));

            var xCol = x.ToColumnMatrix(); // (d x 1)

            lock (_lock)
            {
                // Px = P * x
                var Px = _P * xCol; // (d x 1)

                // denom = ff + x^T P x
                var denom = _forgettingFactor + (xCol.Transpose() * Px).At(0, 0);
                if (denom <= 0.0)
                    denom = 1e-12; // safety against pathological numerical issues

                // k = Px / denom  (d x 1)
                var k = (1.0 / denom) * Px;

                // prediction and error
                var yHat = _W.TransposeThisAndMultiply(x); // (m)
                var e = y - yHat;                          // (m)

                // W <- W + k * e^T
                _W += k * e.ToRowMatrix(); // (d x 1) * (1 x m) => (d x m)

                // P <- (1/ff) * (P - k * x^T * P)
                _P = (1.0 / _forgettingFactor) * (_P - (k * xCol.Transpose() * _P));

                // Optional: keep symmetry (helps numerical stability)
                _P = 0.5 * (_P + _P.Transpose());
            }
        }

        public Vector Predict(Vector x)
        {
            if (x.Count != InputDimension)
                throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

            lock (_lock)
            {
                return _W.TransposeThisAndMultiply(x);
            }
        }

        public double Confidence(Vector x)
        {
            if (x.Count != InputDimension)
                throw new ArgumentException($"Expected input dimension {InputDimension}, got {x.Count}", nameof(x));

            lock (_lock)
            {
                // Similar convention as your RidgeRegression: x^T P x
                var xCol = x.ToColumnMatrix();
                return (xCol.Transpose() * _P * xCol).At(0, 0);
            }
        }

        public RlsState GetState()
        {
            lock (_lock)
            {
                return new RlsState
                {
                    P = _P.Clone(),
                    W = _W.Clone()
                };
            }
        }

        public void SetState(RlsState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            if (state.P.RowCount != InputDimension || state.P.ColumnCount != InputDimension)
                throw new ArgumentException($"P must be {InputDimension}x{InputDimension}");
            if (state.W.RowCount != InputDimension || state.W.ColumnCount != OutputDimension)
                throw new ArgumentException($"W must be {InputDimension}x{OutputDimension}");

            lock (_lock)
            {
                _P = state.P.Clone();
                _W = state.W.Clone();
            }
        }
    }

    public sealed record RlsState
    {
        public required Matrix P { get; init; }
        public required Matrix W { get; init; }
    }
}
