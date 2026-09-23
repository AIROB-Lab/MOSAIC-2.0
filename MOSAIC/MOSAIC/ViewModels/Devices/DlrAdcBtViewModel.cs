using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.Devices;
using MOSAIC.Visualization;

namespace MOSAIC.ViewModels.Devices;

/// <summary>
/// ViewModel for the <see cref="DlrAdcBt"/> device card.
/// </summary>
/// <remarks>
/// <para>
/// <b>Live values are mirrored, never bound directly.</b> <see cref="DlrAdcBt.Info"/> is written
/// from the serial reader thread and its <c>Flag</c> is overwritten on every decoded frame, so an
/// error state survives about one tick and a binding would essentially never render it.
/// <see cref="RefreshLive"/> samples the durable signals — monotonic counters and the last
/// decoded sample — on the UI thread, and everything the card shows is derived from those mirrors.
/// </para>
/// <para>
/// Editable configuration binds straight to the block, which is safe: those properties are only
/// ever written from the UI thread, and the block reads them once, at <see cref="DlrAdcBt.Connect"/>.
/// </para>
/// </remarks>
public partial class DlrAdcBtViewModel : ObservableObject
{
    private readonly DlrAdcBt _block;

    /// <summary>Parser errors within this window raise the fault banner.</summary>
    private static readonly TimeSpan ErrorWindow = TimeSpan.FromSeconds(3);

    // Rate/liveness bookkeeping, all UI-thread.
    private int      _lastFrameCount;
    private int      _lastErrorCount;
    private DateTime _lastRateSample = DateTime.UtcNow;
    private DateTime _lastErrorAt    = DateTime.MinValue;

    /// <summary>Direct access to the model for configuration bindings.</summary>
    public DlrAdcBt Block => _block;

    /// <summary>Live plot of the published sample.</summary>
    public BlockVisualization Viz => _block.Viz;

    #region Mirrored Live State

    /// <summary>Latest status flag reported by the block.</summary>
    [ObservableProperty] private string _flag = "Ready";

    /// <summary>Total frames decoded since the block was created.</summary>
    [ObservableProperty] private int _framesProcessed;

    /// <summary>Total parser faults since the block was created.</summary>
    [ObservableProperty] private int _frameErrors;

    /// <summary>
    /// Smoothed rate at which real frames are decoded off the wire, in Hz.
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="PublishRateHz"/>, which the header chip also shows. The block
    /// publishes on every clock tick whether or not a frame arrived, so the published rate is the
    /// clock rate while this is the rate of genuinely new data. The two agreeing is the healthy
    /// case; this one being lower means that share of published samples are repeats.
    /// </remarks>
    [ObservableProperty] private double _frameRateHz;

    /// <summary>Rate the block publishes at — the clock rate it inherited.</summary>
    [ObservableProperty] private double _publishRateHz;

    /// <summary>Whether frames are actually arriving, as opposed to merely being connected.</summary>
    [ObservableProperty] private bool _isReceiving;

    /// <summary>Whether a parser fault occurred within <see cref="ErrorWindow"/>.</summary>
    [ObservableProperty] private bool _hasRecentErrors;

    /// <summary>One-line summary shown under the card title.</summary>
    [ObservableProperty] private string _statusMessage = "Not connected";

    #endregion

    #region Port Selection

    /// <summary>COM ports available for the picker.</summary>
    [ObservableProperty] private ObservableCollection<string> _availablePorts = [];

    /// <summary>Currently selected port. Writes through to the block.</summary>
    [ObservableProperty] private string? _selectedPort;

    partial void OnSelectedPortChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _block.PortNumber = value;
    }

    #endregion

    /// <summary>Creates a ViewModel bound to <paramref name="block"/>.</summary>
    public DlrAdcBtViewModel(DlrAdcBt block)
    {
        _block = block;

        RefreshPorts();

        // Pre-select the configured port, but only if the machine actually has it — selecting a
        // missing port would write an empty string back over a perfectly good configured one.
        if (AvailablePorts.Contains(_block.PortNumber))
            SelectedPort = _block.PortNumber;
    }

    #region Commands

    /// <summary>Opens the port and starts the board streaming.</summary>
    [RelayCommand]
    private void Connect()
    {
        _block.Connect();
        RefreshLive();
    }

    /// <summary>Stops the board and closes the port.</summary>
    [RelayCommand]
    private void Disconnect()
    {
        _block.Disconnect();
        RefreshLive();
    }

    /// <summary>Closes and reopens the port, re-sending the configuration commands.</summary>
    [RelayCommand]
    private void Reconnect()
    {
        _block.Reconnect();
        RefreshLive();
    }

    /// <summary>Re-enumerates the machine's COM ports.</summary>
    [RelayCommand]
    private void RefreshPorts()
    {
        var selected = SelectedPort;

        AvailablePorts.Clear();
        foreach (var port in DlrAdcBt.RefreshPorts())
            AvailablePorts.Add(port);

        // Re-selecting through the property would write the port back to the block; only do it
        // when the port survived the rescan, so an unplugged dongle leaves the config intact.
        if (selected is not null && AvailablePorts.Contains(selected))
            SelectedPort = selected;
    }

    #endregion

    #region Live Refresh

    /// <summary>
    /// Samples every live signal and recomputes the derived state. Called from the view on a
    /// shared 10 Hz tick; see the card's code-behind.
    /// </summary>
    public void RefreshLive()
    {
        var now = DateTime.UtcNow;

        // Frame rate, from a monotonic counter rather than a transient flag.
        int frames = _block.Info.FramesProcessed;
        double dt = (now - _lastRateSample).TotalSeconds;
        if (dt >= 0.05)
        {
            double instant = Math.Max(0, frames - _lastFrameCount) / dt;
            FrameRateHz = FrameRateHz <= 0 ? instant : (0.3 * instant) + (0.7 * FrameRateHz);
            _lastFrameCount = frames;
            _lastRateSample = now;
        }

        FramesProcessed = frames;
        IsReceiving = FrameRateHz >= 0.5;

        int errors = _block.Info.FrameErrors;
        if (errors > _lastErrorCount) _lastErrorAt = now;
        _lastErrorCount = errors;
        FrameErrors = errors;
        HasRecentErrors = now - _lastErrorAt < ErrorWindow;

        Flag = _block.Info.Flag;
        PublishRateHz = _block.DesiredRate;

        // Data flowing outranks port state: if frames are arriving, show them. Only when nothing
        // is arriving does it matter whether that is "not connected" or "connected but silent".
        StatusMessage = IsReceiving
            ? $"{_block.PortNumber} · {_block.NumOfChannels} ch · " +
              $"{FrameRateHz:F0} Hz in → {PublishRateHz:F0} Hz out"
            : !_block.IsPortOpen
                ? _block.Info.Flag
                : $"{_block.PortNumber} · open, no frames — {_block.Info.Flag}";
    }

    #endregion
}
