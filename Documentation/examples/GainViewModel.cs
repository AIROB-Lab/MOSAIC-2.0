using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.SignalProcessing;

namespace MOSAIC.ViewModels.SignalProcessing;

public partial class GainViewModel : ObservableObject
{
    public GainBlock Block { get; }

    [ObservableProperty]
    private double _gain;

    [ObservableProperty]
    private string _message = "";

    public GainViewModel(GainBlock block)
    {
        Block = block ?? throw new ArgumentNullException(nameof(block));
        _gain = block.Gain;
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
