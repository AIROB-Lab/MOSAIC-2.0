using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.Devices;
namespace MOSAIC.Components.Devices.Muovi;

/// <summary>
/// Observable telemetry model for a Muovi data stream. Bind to this from a device card
/// to display real-time connection status, frame counts, and data loss indicators.
/// </summary>
/// <remarks>
/// Shared by both <see cref="Models.Devices.MuoviSingleProbe"/> and <see cref="Muovi"/> blocks.
/// </remarks>
public partial class MuoviStreamInfo : ObservableObject
{
    /// <summary>Display name of the connected device (e.g. "Muovi SyncStation").</summary>
    [ObservableProperty] private string _deviceName = string.Empty;

    /// <summary>Current pipeline status string (e.g. "Idle", "Streaming", "Disconnected").</summary>
    [ObservableProperty] private string _pipelineStatus = "Idle";

    /// <summary>Number of probes actively contributing data.</summary>
    [ObservableProperty] private int _probesConnected;

    /// <summary>Total number of output channels across all probes.</summary>
    [ObservableProperty] private int _totalChannels;

    /// <summary>Running count of successfully decoded frames since connection.</summary>
    [ObservableProperty] private int _framesCollected;

    /// <summary>Running count of detected packet losses (ramp counter gaps) since connection.</summary>
    [ObservableProperty] private int _packetsLost;

    /// <summary>
    /// Indicates whether data loss is occurring <em>right now</em>. Resets to
    /// <see langword="false"/> automatically when consecutive clean samples arrive.
    /// Bind to a warning banner's <c>IsVisible</c> in the UI.
    /// </summary>
    [ObservableProperty] private bool _dataLoss;

    /// <summary>
    /// Resets all counters and status fields to their initial values.
    /// Call when disconnecting or preparing for a new session.
    /// </summary>
    public void Reset()
    {
        PipelineStatus = "Idle";
        FramesCollected = 0;
        PacketsLost = 0;
        DataLoss = false;
    }
}