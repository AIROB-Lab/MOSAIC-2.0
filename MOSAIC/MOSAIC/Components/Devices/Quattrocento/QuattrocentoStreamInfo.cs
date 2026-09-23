using CommunityToolkit.Mvvm.ComponentModel;

namespace MOSAIC.Components.Devices.Quattrocento;

/// <summary>
/// Observable telemetry and status information for a Quattrocento streaming session.
/// Bind directly to this object from a device card view.
/// </summary>
/// <remarks>
/// <para>
/// All properties are <see cref="ObservablePropertyAttribute"/>-backed and raise
/// <see cref="ObservableObject.PropertyChanged"/> automatically.
/// </para>
/// <para>
/// The reader thread updates these values; the UI thread observes them via data binding.
/// Thread safety is handled through <see cref="System.Threading.Interlocked"/> operations
/// in the block and periodic UI-thread marshalling.
/// </para>
/// </remarks>
public sealed partial class QuattrocentoStreamInfo : ObservableObject
{
    #region Connection

    /// <summary>Human-readable connection status string (e.g. "Disconnected", "Connected — 384 bio + 16 aux @ 10240 Hz").</summary>
    [ObservableProperty] 
    private string _connectionStatus = "Disconnected";

    /// <summary>Whether the device is currently connected over TCP.</summary>
    [ObservableProperty] 
    private bool _isConnected;

    /// <summary>Whether the device is actively streaming data.</summary>
    [ObservableProperty] 
    private bool _isStreaming;

    #endregion

    #region Device Configuration

    /// <summary>Nominal sample rate of the device in Hz (e.g. 10240).</summary>
    [ObservableProperty] 
    private int _nominalRate;

    /// <summary>Total channels per frame including accessory (e.g. 408).</summary>
    [ObservableProperty] 
    private int _totalChannels;

    /// <summary>Number of signal channels in the output matrix (bio + aux, excluding accessory).</summary>
    [ObservableProperty] 
    private int _signalChannelCount;

    /// <summary>Number of bioelectrical (EMG/EEG) channels.</summary>
    [ObservableProperty] 
    private int _bioChannelCount;

    /// <summary>Number of samples per published matrix batch.</summary>
    [ObservableProperty] 
    private int _samplesPerBatch;

    #endregion

    #region Streaming Counters

    /// <summary>Total individual samples received since streaming started.</summary>
    [ObservableProperty] 
    private long _samplesReceived;

    /// <summary>Total matrix batches published since streaming started.</summary>
    [ObservableProperty] 
    private long _framesPublished;

    #endregion

    #region Data Integrity

    /// <summary>Cumulative number of detected dropped samples (ramp counter gaps).</summary>
    [ObservableProperty] 
    private long _droppedSamples;

    /// <summary>
    /// Transient flag — <see langword="true"/> when the most recent ramp check detected a gap.
    /// Resets to <see langword="false"/> on the next clean sample.
    /// </summary>
    [ObservableProperty] 
    private bool _dataLoss;

    /// <summary>
    /// Device internal buffer usage indicator (accessory ch 4).
    /// Values near zero signal the device buffer is nearly full — the PC must read faster.
    /// </summary>
    [ObservableProperty] 
    private int _bufferUsage;

    #endregion

    #region Helpers

    /// <summary>
    /// Resets all streaming counters to zero. Called when a new streaming session begins.
    /// </summary>
    public void ResetCounters()
    {
        SamplesReceived = 0;
        FramesPublished = 0;
        DroppedSamples  = 0;
        DataLoss        = false;
        BufferUsage     = 0;
    }

    #endregion
}