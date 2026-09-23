using CommunityToolkit.Mvvm.ComponentModel;

namespace MOSAIC.Components.Devices.Quattrocento;

/// <summary>
/// Represents a single configurable input on the Quattrocento amplifier
/// (one of IN1..IN8 or MULTIPLE IN1..IN4).
/// </summary>
public sealed partial class QuattrocentoInput : ObservableObject
{
    /// <summary>Display label, e.g. "IN1" or "MULT3".</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Zero-based index within its category (0–7 for IN, 0–3 for MULT).</summary>
    public int Index { get; init; }

    /// <summary>True for MULTIPLE IN (64 ch); false for standard IN (16 ch).</summary>
    public bool IsMultiple { get; init; }

    /// <summary>Number of EMG channels this input contributes when enabled.</summary>
    public int ChannelCount => IsMultiple ? 64 : 16;

    /// <summary>Whether this input is currently enabled in the stream. Default: false.</summary>
    [ObservableProperty] private bool _isEnabled;
}