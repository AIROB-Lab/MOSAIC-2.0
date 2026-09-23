namespace MOSAIC.Components.MachineLearning.Interfaces;

/// <summary>
/// Minimal interface for all online/incremental learners.
/// Only includes what's truly universal across all ML algorithms.
/// </summary>
public interface IOnlineLearner
{
    /// <summary>
    /// Dimensionality of input vectors.
    /// </summary>
    int InputDimension { get; }

    /// <summary>
    /// Resets the learned model to its initial state.
    /// </summary>
    void Reset();
}