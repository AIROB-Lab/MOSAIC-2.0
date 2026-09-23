namespace MOSAIC.Visualization.ScopeMonitor;

/// <summary>
/// A single legend entry for a scope trace: label text + color hex string.
/// Shared across all device ViewModels (Delsys, Muovi, Myo, etc.).
/// </summary>
public sealed record ScopeLegendItem(string Label, string Color);
