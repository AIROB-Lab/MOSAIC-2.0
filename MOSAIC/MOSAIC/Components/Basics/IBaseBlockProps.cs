using MOSAIC.Components.Enums;

namespace MOSAIC.Components.Basics;

/// <summary>
/// Defines common monitoring and identification properties exposed by MOSAIC processing blocks.
/// </summary>
/// <remarks>
/// <para>
/// This interface is primarily intended for UI binding and runtime diagnostics. Implementations typically
/// update <see cref="Status"/> and <see cref="FrequencyText"/> based on internal timing measurements
/// (for example via a tick tracker) and expose <see cref="DesiredRate"/> as a reference value for
/// performance evaluation.
/// </para>
/// <para>
/// <see cref="DesiredRate"/> does not imply throttling by itself; it is used as the expected target rate
/// for status classification (e.g., “lagging” vs. “normal”).
/// </para>
/// </remarks>
public interface IBaseBlockProps
{
    /// <summary>
    /// Gets or sets the display name of the block.
    /// </summary>
    /// <remarks>
    /// Typically assigned from the pipeline configuration to identify the block in UI views and logs.
    /// </remarks>
    string Name { get; set; }

    /// <summary>
    /// Gets or sets the desired processing rate in Hertz used as a reference for status evaluation.
    /// </summary>
    /// <remarks>
    /// This value is used to interpret measured timing (e.g., whether a block is idle or lagging).
    /// It does not throttle execution unless a specific block implementation uses it for scheduling.
    /// </remarks>
    double DesiredRate { get; set; }

    /// <summary>
    /// Gets the current execution status of the block (e.g., Idle, Lagging, Normal).
    /// </summary>
    /// <remarks>
    /// Implementations usually compute this value from timing measurements and internal contention indicators.
    /// </remarks>
    BlockStatus Status { get; }

    /// <summary>
    /// Gets a human-readable representation of the current output rate in Hertz (e.g., "200.0 Hz").
    /// </summary>
    /// <remarks>
    /// Intended for UI display. Implementations typically format this from an internally computed rate estimate.
    /// </remarks>
    string FrequencyText { get; }
}
