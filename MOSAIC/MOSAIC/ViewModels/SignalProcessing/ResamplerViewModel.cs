using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models;
using MOSAIC.Visualization.ScopeMonitor;
using Resampler = MOSAIC.Models.SignalProcessing.Resampler;

namespace MOSAIC.ViewModels.SignalProcessing;

public partial class ResamplerViewModel : ObservableObject
{
    public Resampler Resampler { get; }

    public ResamplerViewModel(Resampler resampler)
    {
        Resampler = resampler;
    }

    [RelayCommand]
    private void ResetStatistics()
    {
        Resampler.ResetStatistics();
    }
}