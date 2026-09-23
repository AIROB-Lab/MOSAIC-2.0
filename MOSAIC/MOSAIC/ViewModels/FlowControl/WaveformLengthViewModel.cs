using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.ViewModels.FlowControl;

/// <summary>
/// ViewModel for the WaveformLength card.
/// </summary>
public partial class WaveformLengthViewModel(WaveformLength waveformLength) : ObservableObject
{
    public WaveformLength WaveformLength => waveformLength;

    /// <summary>
    /// Formula description for display.
    /// </summary>
    public static string Description => "WL = Σ|Δx|";
}