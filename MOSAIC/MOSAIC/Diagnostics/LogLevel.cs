namespace MOSAIC.Diagnostics;

/// <summary>
/// Severity of a log entry, ordered so that the whole level gate is one integer comparison.
/// </summary>
/// <remarks>
/// The numeric values are part of the contract: <see cref="Log.IsEnabled"/> is a single
/// <c>&gt;=</c> against <see cref="Log.MinimumLevel"/>, which is what keeps a disabled call site
/// down to one volatile read on a data thread.
/// </remarks>
public enum LogLevel
{
    /// <summary>Per-packet detail. Off in a Release build unless the student turns it on.</summary>
    Debug = 0,

    /// <summary>Lifecycle: a device connected, a pipeline started, a file was written.</summary>
    Info = 1,

    /// <summary>Something went wrong but the run continues, possibly degraded.</summary>
    Warn = 2,

    /// <summary>An operation failed. This is what a bug report should be searched for.</summary>
    Error = 3,

    /// <summary>
    /// Logging off. Valid only as a <see cref="Log.MinimumLevel"/>; no entry ever carries it,
    /// because an entry at "None" could never pass its own gate.
    /// </summary>
    None = 4
}
