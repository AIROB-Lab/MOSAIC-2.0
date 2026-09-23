#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Components.Devices.Muovi;
using MOSAIC.Models.Devices;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.ViewModels.Devices.Muovi;

/// <summary>
/// ViewModel for the <see cref="MuoviSingleProbe"/> device card.
/// Exposes signal mode and IMU toggle for UI configuration before streaming.
/// </summary>
public partial class MuoviSingleProbeViewModel : ObservableObject
{
    private readonly MuoviSingleProbe _block;

    public MuoviSingleProbe Block => _block;
    public string Name => _block.Name;

    // ── Mode selection (bound to ComboBox) ──────────────────────

    /// <summary>Available signal modes for the ComboBox.</summary>
    public ObservableCollection<SignalModeOption> SignalModes { get; } = new()
    {
        new("EMG",             MuoviSignalMode.Emg),
        new("EMG (Low Gain)",  MuoviSignalMode.EmgLowGain),
        new("EEG",             MuoviSignalMode.Eeg),
    };

    /// <summary>Currently selected signal mode. Writes through to the block.</summary>
    [ObservableProperty] private SignalModeOption _selectedSignalMode;

    /// <summary>IMU streaming toggle. Writes through to the block.</summary>
    [ObservableProperty] private bool _streamImu;

    // ── Status display ──────────────────────────────────────────

    [ObservableProperty] private string _toggleLabel = "Start";
    [ObservableProperty] private string _modeDisplay = "";
    [ObservableProperty] private bool _canConfigure = true;

    /// <summary>Channel labels for the EMG/EEG scope legend with trace colors.</summary>
    public ObservableCollection<ScopeLegendItem> EmgChannelLabels { get; } = new();

    /// <summary>Channel labels for the IMU scope legend with trace colors.</summary>
    public ObservableCollection<ScopeLegendItem> ImuChannelLabels { get; } = new();

    public MuoviSingleProbeViewModel(MuoviSingleProbe block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));

        // Initialise from block state
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
            if (args.PropertyName is nameof(MuoviSingleProbe.HasEmg)
                                  or nameof(MuoviSingleProbe.HasImu))
                OnPropertyChanged(nameof(Block));
        };
    }

    // Write-through when UI changes selection
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
            Console.WriteLine($"[MuoviSingleVM] Error: {ex.Message}");
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



// ═══════════════════════════════════════════════════════════════════
//  Shared helper
// ═══════════════════════════════════════════════════════════════════

/// <summary>ComboBox item for signal mode selection.</summary>
public sealed record SignalModeOption(string Label, MuoviSignalMode Mode)
{
    public override string ToString() => Label;
}
