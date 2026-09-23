using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.Components.MachineLearning.Interfaces
{
    /// <summary>
    /// Interface for online/incremental regression models.
    /// Maps input vectors to output vectors.
    /// </summary>
    public interface IRegressor : ISupervisedLearner
    {
        /// <summary>
        /// Dimensionality of output vectors.
        /// </summary>
        int OutputDimension { get; }

        /// <summary>
        /// Updates the model with a single training sample.
        /// </summary>
        /// <param name="x">Input vector (features).</param>
        /// <param name="y">Target output vector.</param>
        void Update(Vector x, Vector y);

        /// <summary>
        /// Predicts output for a given input.
        /// </summary>
        /// <param name="x">Input vector.</param>
        /// <returns>Predicted output vector.</returns>
        Vector Predict(Vector x);

        /// <summary>
        /// Evaluates prediction confidence for a given input.
        /// Higher values typically indicate more certain predictions.
        /// </summary>
        /// <param name="x">Input vector.</param>
        /// <returns>Confidence score.</returns>
        double Confidence(Vector x);
    }

    /// <summary>
    /// State container for Ridge-based regressors.
    /// </summary>
    public sealed record RidgeState
    {
        public required MathNet.Numerics.LinearAlgebra.Matrix<double> Ainv { get; init; }
        public required MathNet.Numerics.LinearAlgebra.Matrix<double> B { get; init; }
    }
}