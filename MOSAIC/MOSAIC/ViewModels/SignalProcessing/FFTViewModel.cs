using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.SignalProcessing;

namespace MOSAIC.ViewModels.SignalProcessing;

/// <summary>
/// View model for an <see cref="FFT"/> block: surfaces its live metrics and lets the user
/// change the window function, output mode and dB parameters at runtime.
/// </summary>
/// <remarks>
/// Threading: the block raises <see cref="INotifyPropertyChanged"/> events from its processing
/// thread. CommunityToolkit's generated setters and <c>OnPropertyChanged</c> are thread-safe —
/// Avalonia marshals the change notifications to the UI thread — so no explicit <c>Dispatcher</c>
/// hop is needed here. The block subscription is released in <see cref="Dispose"/>.
/// </remarks>
public partial class FFTViewModel : ObservableObject, IDisposable
{
    private const string AllChannelsLabel = "All (avg)";
    private const string ChannelPrefix = "Ch ";

    private readonly FFT _fft;
    private bool _disposed;

    [ObservableProperty]
    private FFT.WindowType _selectedWindow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDbMode))]
    private FFT.OutputMode _selectedOutputMode;

    [ObservableProperty]
    private double _dbRef;

    [ObservableProperty]
    private double _dbFloor;

    [ObservableProperty]
    private string _selectedChannelOption = AllChannelsLabel;

    public FFTViewModel(FFT fft)
    {
        _fft = fft ?? throw new ArgumentNullException(nameof(fft));

        _selectedWindow = _fft.Window;
        _selectedOutputMode = _fft.Mode;
        _dbRef = _fft.DbRef;
        _dbFloor = _fft.DbFloor;

        _fft.PropertyChanged += OnBlockPropertyChanged;

        RebuildChannelList();
    }

    /// <summary>The underlying block, exposed so the view can bind its spectrogram.</summary>
    public FFT Block => _fft;

    public string Name => _fft.Name;

    /// <summary>Live execution status, surfaced to the status badge.</summary>
    public MOSAIC.Components.Enums.BlockStatus BlockStatus => _fft.Status;

    public string FftDescription => _fft.FftDescription;

    public int FftSize => _fft.CurrentFftSize;
    public int OutputBins => _fft.OutputBinCount;
    public double FrequencyResolution => _fft.FrequencyResolution;
    public int ChannelCount => _fft.ChannelCount;

    /// <summary>Window functions offered to the user, taken straight from the block's enum.</summary>
    public IReadOnlyList<FFT.WindowType> AvailableWindows { get; } = Enum.GetValues<FFT.WindowType>();

    /// <summary>Output modes offered to the user, taken straight from the block's enum.</summary>
    public IReadOnlyList<FFT.OutputMode> AvailableOutputModes { get; } = Enum.GetValues<FFT.OutputMode>();

    /// <summary>Whether the dB controls should be visible (only when the mode is dB).</summary>
    public bool IsDbMode => _fft.Mode == FFT.OutputMode.DB;

    public ObservableCollection<string> ChannelOptions { get; } = new() { AllChannelsLabel };

    partial void OnSelectedWindowChanged(FFT.WindowType value) => _fft.Window = value;

    partial void OnSelectedOutputModeChanged(FFT.OutputMode value) => _fft.Mode = value;

    partial void OnDbRefChanged(double value) => _fft.DbRef = value;

    partial void OnDbFloorChanged(double value) => _fft.DbFloor = value;

    partial void OnSelectedChannelOptionChanged(string value)
    {
        if (string.IsNullOrEmpty(value) || value == AllChannelsLabel)
            _fft.SelectedChannel = -1;   // -1 ⇒ average across all channels
        else if (value.StartsWith(ChannelPrefix, StringComparison.Ordinal) &&
                 int.TryParse(value.AsSpan(ChannelPrefix.Length), out var channel))
            _fft.SelectedChannel = channel;
    }

    /// <summary>
    /// Rebuilds the channel dropdown to match the block's current channel count,
    /// preserving the current selection where possible.
    /// </summary>
    private void RebuildChannelList()
    {
        var current = SelectedChannelOption;

        ChannelOptions.Clear();
        ChannelOptions.Add(AllChannelsLabel);
        for (var i = 0; i < _fft.ChannelCount; i++)
            ChannelOptions.Add($"{ChannelPrefix}{i}");

        SelectedChannelOption = ChannelOptions.Contains(current) ? current : AllChannelsLabel;
    }

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;

        switch (e.PropertyName)
        {
            case nameof(FFT.CurrentFftSize):
                OnPropertyChanged(nameof(FftSize));
                break;
            case nameof(FFT.OutputBinCount):
                OnPropertyChanged(nameof(OutputBins));
                break;
            case nameof(FFT.FrequencyResolution):
                OnPropertyChanged(nameof(FrequencyResolution));
                break;
            case nameof(FFT.ChannelCount):
                OnPropertyChanged(nameof(ChannelCount));
                RebuildChannelList();
                break;
            case nameof(FFT.FftDescription):
                OnPropertyChanged(nameof(FftDescription));
                break;
            case nameof(FFT.Status):
                OnPropertyChanged(nameof(BlockStatus));
                break;
            case nameof(FFT.DbRef):
                DbRef = _fft.DbRef;
                break;
            case nameof(FFT.DbFloor):
                DbFloor = _fft.DbFloor;
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _fft.PropertyChanged -= OnBlockPropertyChanged;
    }
}