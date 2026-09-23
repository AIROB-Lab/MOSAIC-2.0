using CommunityToolkit.Mvvm.ComponentModel;

namespace MOSAIC.Components.Devices.Delsys;

/// <summary>
/// Observable stream telemetry for a Delsys pipeline.
/// Bound directly in the Avalonia card view.
/// </summary>
public partial class DelsysStreamInfo : ObservableObject
{
    [ObservableProperty] private string _deviceName = string.Empty;
    [ObservableProperty] private string _pipelineStatus = "Idle";
    [ObservableProperty] private int    _sensorsConnected;
    [ObservableProperty] private int    _totalChannels;
    [ObservableProperty] private string _streamTime = "0.0 s";
    [ObservableProperty] private int    _packetsLost;
    [ObservableProperty] private int    _framesCollected;

    /// <summary>Resets all counters to their initial state.</summary>
    public void Reset()
    {
        PipelineStatus  = "Idle";
        StreamTime      = "0.0 s";
        PacketsLost     = 0;
        FramesCollected = 0;
    }
}