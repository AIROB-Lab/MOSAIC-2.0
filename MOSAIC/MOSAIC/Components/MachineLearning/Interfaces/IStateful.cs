namespace MOSAIC.Components.MachineLearning.Interfaces;

/// <summary>
/// For learners that support serializable state.
/// </summary>
/// <typeparam name="TState">The type representing the learner's internal state.</typeparam>
public interface IStateful<TState> where TState : class
{
    /// <summary>
    /// Gets a copy of the current internal state.
    /// </summary>
    TState GetState();

    /// <summary>
    /// Restores the learner from a previously saved state.
    /// </summary>
    void SetState(TState state);
}