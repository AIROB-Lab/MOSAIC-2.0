namespace MOSAIC.Visualization;

/// <summary>
/// The lifecycle surface every monitor exposes to its view. A monitor owns the data
/// engine (buffers, timers, channel configuration); the view drives it through these
/// members as the rendering surface is created and torn down.
/// </summary>
public interface IMonitor
{
    /// <summary>Stops data flow and rendering updates.</summary>
    void Pause();

    /// <summary>Resumes data flow and rendering updates.</summary>
    void Resume();

    /// <summary>Clears buffered data and any accumulated display state.</summary>
    void ResetData();

    /// <summary>Re-applies theme-dependent colours to the monitor's paints.</summary>
    void UpdateThemeColors();
}