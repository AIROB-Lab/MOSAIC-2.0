#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Components.Devices.Muovi;
using MOSAIC.Models.Devices;
using MOSAIC.ViewModels.Devices.Muovi;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.ViewModels.Devices;

/// <summary>
/// ViewModel for the <see cref="Muovi"/> SyncStation device card.
/// </summary>
public partial class MuoviViewModel : ObservableObject
{
    private readonly Models.Devices.Muovi _block;

    public Models.Devices.Muovi Block => _block;
    public string Name => _block.Name;
    public string ProbeList => string.Join(", ", _block.ProbeIndices.Select(i => $"P{i}"));
    public int ProbeCount => _block.ProbeIndices.Count;

    public ObservableCollection<SignalModeOption> SignalModes { get; } = new()
    {
        new("EMG",             MuoviSignalMode.Emg),
        new("EMG (Low Gain)",  MuoviSignalMode.EmgLowGain),
        new("EEG",             MuoviSignalMode.Eeg),
    };

    [ObservableProperty] private SignalModeOption _selectedSignalMode;
    [ObservableProperty] private bool _streamImu;
    [ObservableProperty] private string _toggleLabel = "Start";
    [ObservableProperty] private string _modeDisplay = "";
    [ObservableProperty] private bool _canConfigure = true;

    public ObservableCollection<ScopeLegendItem> EmgChannelLabels { get; } = new();
    public ObservableCollection<ScopeLegendItem> ImuChannelLabels { get; } = new();

    public MuoviViewModel(Models.Devices.Muovi block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));

        _selectedSignalMode = SignalModes[0];
        foreach (var opt in SignalModes)
        {
            if (opt.Mode == _block.SignalMode)
            { _selectedSignalMode = opt; break; }
        }
        _streamImu = _block.StreamImu;

        UpdateModeDisplay();

        _block.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(_block.HasEmg) or nameof(_block.HasImu))
                OnPropertyChanged(nameof(Block));
        };
    }

    partial void OnSelectedSignalModeChanged(SignalModeOption value)
    {
        _block.SignalMode = value.Mode;
        UpdateModeDisplay();
    }

    partial void OnStreamImuChanged(bool value)
    {
        _block.StreamImu = value;
        UpdateModeDisplay();
    }

    [RelayCommand]
    private async Task ToggleStreamingAsync()
    {
        try
        {
            if (_block.IsStreaming)
            {
                _block.Disconnect();
                ToggleLabel  = "Start";
                CanConfigure = true;
            }
            else
            {
                CanConfigure = false;
                ToggleLabel  = "Connecting...";
                await _block.ConnectAsync();
                ToggleLabel  = "Stop";
                RefreshLabels();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MuoviVM] Error: {ex.Message}");
            ToggleLabel  = "Start";
            CanConfigure = true;
        }
    }

    private void UpdateModeDisplay()
    {
        var label = SelectedSignalMode.Label;
        ModeDisplay = StreamImu ? $"{label} + IMU" : label;
    }

    private void RefreshLabels()
    {
        EmgChannelLabels.Clear();
        for (int i = 0; i < _block.EmgChannelLabels.Count; i++)
            EmgChannelLabels.Add(new ScopeLegendItem(
                _block.EmgChannelLabels[i], ScopeMonitor.GetChannelColorHex(i)));

        ImuChannelLabels.Clear();
        for (int i = 0; i < _block.ImuChannelLabels.Count; i++)
            ImuChannelLabels.Add(new ScopeLegendItem(
                _block.ImuChannelLabels[i], ScopeMonitor.GetChannelColorHex(i)));
    }
}

