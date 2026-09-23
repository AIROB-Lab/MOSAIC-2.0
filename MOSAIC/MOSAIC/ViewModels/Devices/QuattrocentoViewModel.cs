#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Components.Devices.Quattrocento;
using MOSAIC.Models.Devices;

namespace MOSAIC.ViewModels.Devices;

/// <summary>
/// ViewModel for the <see cref="Models.Devices.Quattrocento"/> device card.
/// Sampling frequency + decimator selection; per-input toggles bind directly
/// to <c>Block.InsCollection</c> / <c>Block.MultsCollection</c>.
/// </summary>
public partial class QuattrocentoViewModel : ObservableObject
{
    private readonly Models.Devices.Quattrocento _block;

    public Models.Devices.Quattrocento Block => _block;
    public string Name => _block.Name;

    #region Configuration Options

    public ObservableCollection<SamplingFrequencyOption> SamplingFrequencies { get; } = new()
    {
        new("512 Hz",   0),
        new("2048 Hz",  1),
        new("5120 Hz",  2),
        new("10240 Hz", 3),
    };

    [ObservableProperty] private SamplingFrequencyOption _selectedFrequency;
    [ObservableProperty] private bool                    _decimatorEnabled = true;
    [ObservableProperty] private bool                    _canConfigure     = true;

    public bool IsConnected => _block.StreamInfo.IsConnected;

    #endregion

    #region Constructor

    public QuattrocentoViewModel(Models.Devices.Quattrocento block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));

        _selectedFrequency = SamplingFrequencies[Math.Clamp(block.FsampIndex, 0, 3)];
        _decimatorEnabled  = block.DecimatorEnabled;

        _block.StreamInfo.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(QuattrocentoStreamInfo.IsConnected))
            {
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(Block));
            }

            if (args.PropertyName is nameof(QuattrocentoStreamInfo.IsStreaming)
                                  or nameof(QuattrocentoStreamInfo.ConnectionStatus)
                                  or nameof(QuattrocentoStreamInfo.DataLoss)
                                  or nameof(QuattrocentoStreamInfo.DroppedSamples)
                                  or nameof(QuattrocentoStreamInfo.BufferUsage)
                                  or nameof(QuattrocentoStreamInfo.SamplesReceived)
                                  or nameof(QuattrocentoStreamInfo.FramesPublished)
                                  or nameof(QuattrocentoStreamInfo.SignalChannelCount))
                OnPropertyChanged(nameof(Block));
        };
    }

    #endregion

    #region Write-through

    partial void OnSelectedFrequencyChanged(SamplingFrequencyOption value)
        => _block.FsampIndex = value.Index;

    partial void OnDecimatorEnabledChanged(bool value)
        => _block.DecimatorEnabled = value;

    #endregion

    #region Commands

    [RelayCommand]
    private async Task ToggleStreamingAsync()
    {
        try
        {
            if (_block.StreamInfo.IsConnected)
            {
                _block.Disconnect();
                CanConfigure = true;
            }
            else
            {
                CanConfigure = false;
                await _block.ConnectAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QuattrocentoVM] Error: {ex.Message}");
            CanConfigure = true;
        }
    }

    #endregion
}

/// <summary>ComboBox item for sampling frequency selection.</summary>
public sealed record SamplingFrequencyOption(string Label, int Index)
{
    public override string ToString() => Label;
}