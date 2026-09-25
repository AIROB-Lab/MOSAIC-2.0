using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.SignalProcessing;

namespace MOSAIC.ViewModels.SignalProcessing;

public sealed partial class GainViewModel : ObservableObject
{
    private readonly GainBlock _block;

    public GainBlock Block => _block;

    [ObservableProperty]
    private double _gain;

    [ObservableProperty]
    private string _message = string.Empty;

    public GainViewModel(GainBlock block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));
        _gain = _block.Gain;
    }

    [RelayCommand]
    private void Apply()
    {
        if (!double.IsFinite(Gain))
        {
            Message = "Enter a finite gain.";
            return;
        }

        Block.SetGain(Gain);
        Message = $"Applied gain: {Block.Gain:G}";
    }
}
