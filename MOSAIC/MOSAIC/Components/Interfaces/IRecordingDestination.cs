namespace MOSAIC.Components.Interfaces;

/// <summary>
/// Supplies the folder a recording block writes into, so blocks carry a boolean rather than a path.
/// </summary>
/// <remarks>
/// Declared here rather than taken as a concrete service so <c>Components</c> keeps its existing
/// independence from <c>MOSAIC.Services</c>; the implementation is
/// <c>MOSAIC.Services.RecordingService</c>, registered as a singleton at startup.
/// </remarks>
public interface IRecordingDestination
{
    /// <summary>True once a destination is set and blocks can actually record.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Folder for the current run, created on first access, or <see langword="null"/> when no
    /// destination has been chosen.
    /// </summary>
    string? SessionFolder { get; }
}
