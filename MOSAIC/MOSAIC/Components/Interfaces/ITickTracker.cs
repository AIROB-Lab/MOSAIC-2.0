using MOSAIC.Components.Enums;

namespace MOSAIC.Components.Interfaces;

/// <summary>
/// Defines a tick tracker used to measure timing, estimate rate, and derive execution status for a block.
/// </summary>
/// <remarks>
/// <para>
/// A tick represents a recurring event of interest (for example: a successful publish, a scheduler cycle,
/// or an input-processing step). Implementations record ticks and provide derived metrics such as
/// <see cref="CurrentRate"/> and a higher-level <see cref="BlockStatus"/> via <see cref="GetStatus"/>.
/// </para>
/// <para>
/// The <paramref name="desiredRate"/> parameters are used as reference values for classification (idle/lagging/status).
/// They do not imply throttling; they are used to interpret measured timing.
/// </para>
/// </remarks>
public interface ITickTracker
{
    /// <summary>
    /// Records a tick event at the current time.
    /// </summary>
    /// <remarks>
    /// Implementations should use a monotonic time source when possible to ensure stable rate estimation.
    /// </remarks>
    void RecordTick();

    /// <summary>
    /// Gets the current estimated tick rate in Hertz (ticks per second).
    /// </summary>
    /// <remarks>
    /// Implementations may return <c>0</c> until sufficient tick history is available to compute a stable estimate.
    /// </remarks>
    double CurrentRate { get; }

    /// <summary>
    /// Determines whether the stream of ticks is considered idle relative to a desired rate.
    /// </summary>
    /// <param name="desiredRate">Desired rate in Hertz used to compute an inactivity threshold.</param>
    /// <returns>
    /// <see langword="true"/> if ticks have not been recorded recently enough to satisfy the idle threshold;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The exact definition of “idle” is implementation-specific, but it typically means that no ticks have
    /// occurred for a timeout derived from <paramref name="desiredRate"/>.
    /// </remarks>
    bool IsIdle(double desiredRate);

    /// <summary>
    /// Determines whether the current estimated tick rate is lagging relative to a desired rate.
    /// </summary>
    /// <param name="desiredRate">Desired rate in Hertz used as the reference.</param>
    /// <returns>
    /// <see langword="true"/> if the measured rate is below an implementation-defined threshold of
    /// <paramref name="desiredRate"/>; otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Implementations commonly apply a tolerance to avoid oscillation due to jitter.
    /// </remarks>
    bool IsLagging(double desiredRate);

    /// <summary>
    /// Gets the timestamp (seconds) of the most recently recorded tick.
    /// </summary>
    /// <remarks>
    /// The time base is implementation-specific. Implementations should document whether this is
    /// monotonic time, wall-clock time, or a UNIX-like representation.
    /// </remarks>
    double LastTick { get; }

    /// <summary>
    /// Computes the current <see cref="BlockStatus"/> based on tick history, desired rate, and stumble state.
    /// </summary>
    /// <param name="desiredRate">Desired rate in Hertz used for idle/lagging classification.</param>
    /// <param name="isStumbling">
    /// Optional flag indicating a temporary stumble condition detected by the caller (e.g., lock contention or missed deadlines).
    /// </param>
    /// <returns>A <see cref="BlockStatus"/> value representing the current execution state.</returns>
    /// <remarks>
    /// Implementations typically prioritize <paramref name="isStumbling"/> over rate-based classifications to
    /// surface short-term instability.
    /// </remarks>
    BlockStatus GetStatus(double desiredRate, bool isStumbling = false);
}
