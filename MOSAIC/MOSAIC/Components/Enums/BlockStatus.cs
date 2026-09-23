namespace MOSAIC.Components.Enums;

/// <summary>
/// Represents the execution status of a Block, as reported by a tick tracker.
/// </summary>
/// <remarks>
/// <para>
/// This enumeration is used by <see cref="MOSAIC.Components.Interfaces.ITickTracker"/> 
/// (and related monitoring components) to classify a Block’s runtime behavior relative to its expected rate.
/// </para>
/// </remarks>
public enum BlockStatus
{
    /// <summary>
    /// The Block is running normally, keeping up with the desired rate.
    /// </summary>
    Normal,

    /// <summary>
    /// The Block is falling behind its desired rate (producing ticks slower than expected).
    /// </summary>
    Lagging,

    /// <summary>
    /// The Block is in a temporary stumbling state 
    /// (short-term irregular behavior, often recoverable without intervention).
    /// </summary>
    Stumbling,

    /// <summary>
    /// The Block is idle (no ticks or activity detected relative to the desired rate).
    /// </summary>
    Idle,
    
    Learning,
    
    Stable
}