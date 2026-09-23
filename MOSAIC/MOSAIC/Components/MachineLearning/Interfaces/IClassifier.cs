using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.Components.MachineLearning.Interfaces;

/// <summary>
/// Interface for online/incremental classification models.
/// Maps input vectors to discrete class labels.
/// </summary>
public interface IClassifier : ISupervisedLearner
{
    /// <summary>
    /// Dimensionality of output (same as NumClasses).
    /// </summary>
    int OutputDimension { get; }

    /// <summary>
    /// Number of classes the classifier can distinguish.
    /// </summary>
    int NumClasses { get; }

    /// <summary>
    /// Updates the model with a single labeled sample.
    /// </summary>
    /// <param name="x">Input vector (features).</param>
    /// <param name="label">Class label (0 to NumClasses-1).</param>
    void Update(Vector x, int label);

    /// <summary>
    /// Predicts the most likely class for a given input.
    /// </summary>
    /// <param name="x">Input vector.</param>
    /// <returns>Predicted class label.</returns>
    int Predict(Vector x);

    /// <summary>
    /// Predicts class probabilities for a given input.
    /// </summary>
    /// <param name="x">Input vector.</param>
    /// <returns>Array of probabilities, one per class (sums to 1).</returns>
    double[] PredictProbabilities(Vector x);

    /// <summary>
    /// Evaluates prediction confidence for a given input.
    /// Typically the maximum class probability.
    /// </summary>
    /// <param name="x">Input vector.</param>
    /// <returns>Confidence score between 0 and 1.</returns>
    double Confidence(Vector x);
}

/// <summary>
/// State container for Softmax-based classifiers.
/// </summary>
public sealed record SoftmaxState
{
    public required MathNet.Numerics.LinearAlgebra.Matrix<double> W { get; init; }
    public required MathNet.Numerics.LinearAlgebra.Vector<double> B { get; init; }
    public int SampleCount { get; init; }
}