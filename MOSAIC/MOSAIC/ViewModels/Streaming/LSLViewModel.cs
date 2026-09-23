using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.Streaming;
using MOSAIC.Visualization;

namespace MOSAIC.ViewModels.Streaming;

/// <summary>
/// ViewModel for <see cref="MOSAIC.Models.Streaming.LSL"/>. Exposes mode, stream metadata,
/// editable settings, start/stop commands, stats, and visualization binding.
/// </summary>
public partial class LSLViewModel : ObservableObject, IDisposable
{
    #region Fields

    [ObservableProperty]
    private Models.Streaming.LSL _block;
    private bool _disposed;

    #endregion

    #region Properties

    

    /// <summary>Whether this block is in Inlet mode.</summary>
    public bool IsInlet => _block.Mode == LslMode.Inlet;

    /// <summary>Whether this block is in Outlet mode.</summary>
    public bool IsOutlet => _block.Mode == LslMode.Outlet;

    /// <summary>Mode label for the category badge.</summary>
    public string ModeLabel => _block.Mode == LslMode.Inlet ? "INLET" : "OUTLET";
    

    #endregion

    #region Observable Properties — State

    /// <summary>Whether the block is currently active.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopLabel))]
    private bool _isActive;

    /// <summary>Running sample counter for stats display.</summary>
    [ObservableProperty] private long _sampleCount;

    /// <summary>Start/Stop button label (inlet only).</summary>
    public string StartStopLabel => IsActive ? "Stop" : "Start";

    #endregion

    #region Observable Properties — Editable Settings

    /// <summary>Editable stream name.</summary>
    [ObservableProperty] private string _editStreamName;

    /// <summary>Editable stream type.</summary>
    [ObservableProperty] private string _editStreamType;

    /// <summary>Editable nominal rate (outlet) or resolve timeout (inlet).</summary>
    [ObservableProperty] private double _editRateOrTimeout;

    /// <summary>Editable channel count (outlet) or max chunk length (inlet).</summary>
    [ObservableProperty] private int _editChannelsOrChunkLen;

    /// <summary>Rate/timeout label depending on mode.</summary>
    public string RateOrTimeoutLabel => IsInlet ? "Timeout (s)" : "Rate (Hz)";

    /// <summary>Channels/chunk label depending on mode.</summary>
    public string ChannelsOrChunkLabel => IsInlet ? "Max Chunk" : "Channels";

    /// <summary>Whether the settings panel is expanded.</summary>
    [ObservableProperty] private bool _isSettingsExpanded;

    #endregion

    #region Constructor

    /// <summary>
    /// Initialises a new <see cref="LSLViewModel"/> and wires it to the given block.
    /// </summary>
    public LSLViewModel(Models.Streaming.LSL block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));
        

        // Mirror initial state
        IsActive = _block.IsActive;

        // Mirror editable settings from block
        _editStreamName = _block.StreamName;
        _editStreamType = _block.StreamType;
        _editRateOrTimeout = _block.Mode == LslMode.Outlet
            ? _block.NominalRate
            : _block.ResolveTimeout;
        _editChannelsOrChunkLen = _block.Mode == LslMode.Outlet
            ? _block.ResolvedChannels
            : _block.MaxChunkLen;

        // Keep in sync
        _block.PropertyChanged += OnBlockPropertyChanged;
    }

    #endregion

    #region Commands

    /// <summary>Starts the inlet reader.</summary>
    [RelayCommand]
    private void StartStreaming()
    {
        if (IsInlet)
            Block.Start();
    }

    /// <summary>Stops the inlet reader.</summary>
    [RelayCommand]
    private void StopStreaming()
    {
        if (IsInlet)
            Block.Stop();
    }

    /// <summary>Applies edited settings to the block. Only effective when not active.</summary>
    [RelayCommand]
    private void ApplySettings()
    {
        _block.ApplySettings(_editStreamName, _editStreamType, _editRateOrTimeout, _editChannelsOrChunkLen);
        OnPropertyChanged(nameof(Block));
    }

    /// <summary>Toggles the settings panel visibility.</summary>
    [RelayCommand]
    private void ToggleSettingsExpanded() => IsSettingsExpanded = !IsSettingsExpanded;

    #endregion

    #region Block Sync

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;

        if (e.PropertyName == nameof(Block.IsActive))
            IsActive = Block.IsActive;
        else
            OnPropertyChanged(nameof(Block));
    }

    private void OnSampleReceived() => SampleCount++;

    #endregion

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Block.PropertyChanged -= OnBlockPropertyChanged;
        Block.Viz?.Dispose();
    }

    #endregion
}