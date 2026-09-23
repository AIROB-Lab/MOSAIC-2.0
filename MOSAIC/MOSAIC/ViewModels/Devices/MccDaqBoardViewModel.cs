using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.Devices;
using MOSAIC.Visualization;

namespace MOSAIC.ViewModels.Devices;

/// <summary>
/// ViewModel for the <see cref="MccDaqBoard"/> device card.
/// </summary>
/// <remarks>
/// Configuration binds straight to the block — those properties are only ever written from the UI
/// thread, and the block reads them once, at <see cref="MccDaqBoard.Connect"/>. Only the packet
/// counter is mirrored, because it advances on the pipeline thread at the clock rate;
/// <see cref="RefreshLive"/> samples it on the card's 10 Hz tick.
/// </remarks>
public partial class MccDaqBoardViewModel : ObservableObject
{
    private readonly MccDaqBoard _block;

    private long     _lastPacketCount;
    private DateTime _lastSampledAt = DateTime.UtcNow;

    /// <summary>Direct access to the model for configuration bindings.</summary>
    public MccDaqBoard Block => _block;

    /// <summary>Live plot of the published packet.</summary>
    public BlockVisualization Viz => _block.Viz;

    /// <summary>Input ranges offered by the range picker.</summary>
    public string[] KnownRanges => MccDaqBoard.KnownRanges;

    /// <summary>Boards InstaCal has configured, as "number: name".</summary>
    [ObservableProperty] private ObservableCollection<string> _availableBoards = [];

    /// <summary>Packets acquired, sampled on the card's tick.</summary>
    [ObservableProperty] private long _packetsAcquired;

    /// <summary>Smoothed rate at which real packets come off the board, in Hz.</summary>
    /// <remarks>
    /// The block publishes on every clock tick whether or not a packet was ready, so this being
    /// lower than the clock rate means that share of published packets are repeats.
    /// </remarks>
    [ObservableProperty] private double _packetRateHz;

    /// <summary>Whether packets are actually arriving, as opposed to merely being connected.</summary>
    [ObservableProperty] private bool _isReceiving;

    /// <summary>What the current settings would ask the board for.</summary>
    [ObservableProperty] private string _scanSummary = string.Empty;

    /// <summary>Creates a ViewModel bound to <paramref name="block"/>.</summary>
    public MccDaqBoardViewModel(MccDaqBoard block)
    {
        _block = block;
        RefreshBoards();
        UpdateSummary();
    }

    /// <summary>Starts the background scan.</summary>
    [RelayCommand]
    private void Connect() { _block.Connect(); UpdateSummary(); }

    /// <summary>Stops the scan and frees the driver buffer.</summary>
    [RelayCommand]
    private void Disconnect() => _block.Disconnect();

    /// <summary>Restarts the scan, picking up changed configuration.</summary>
    [RelayCommand]
    private void Reconnect() { _block.Reconnect(); UpdateSummary(); }

    /// <summary>Re-enumerates the boards InstaCal has configured.</summary>
    [RelayCommand]
    private void RefreshBoards()
    {
        AvailableBoards.Clear();
        foreach (var board in MccDaqBoard.AvailableBoards())
            AvailableBoards.Add(board);
    }

    /// <summary>
    /// Recomputes the line describing what the current settings would acquire.
    /// </summary>
    /// <remarks>
    /// The scan rate is a product of clock rate, scans per packet and channel count, and getting
    /// it wrong is the likeliest misconfiguration of this block. Showing the derived figure means
    /// it gets checked before Connect, rather than inferred afterwards from a signal that is
    /// subtly wrong.
    /// </remarks>
    public void UpdateSummary()
    {
        double scanRate = _block.RequestedScanRate;

        ScanSummary = scanRate <= 0
            ? "Connect a Clock to set the acquisition rate."
            : $"ch {_block.LowChannel}-{_block.HighChannel} ({_block.ChannelCount}) · " +
              $"{scanRate:F0} scans/s · {_block.ScansPerPacket} per tick · " +
              $"{(_block.ScansPerPacket == 1 ? "vector" : $"{_block.ScansPerPacket}×{_block.ChannelCount} matrix")} out";
    }

    /// <summary>
    /// Samples the packet counter and derives the acquisition rate. Called from the view on a
    /// shared 10 Hz tick; see the card's code-behind.
    /// </summary>
    public void RefreshLive()
    {
        var now = DateTime.UtcNow;
        double dt = (now - _lastSampledAt).TotalSeconds;

        long packets = _block.PacketsAcquired;
        if (dt >= 0.05)
        {
            double instant = Math.Max(0, packets - _lastPacketCount) / dt;
            PacketRateHz = PacketRateHz <= 0 ? instant : (0.3 * instant) + (0.7 * PacketRateHz);
            _lastPacketCount = packets;
            _lastSampledAt   = now;
        }

        PacketsAcquired = packets;
        IsReceiving     = PacketRateHz >= 0.5;
    }
}
