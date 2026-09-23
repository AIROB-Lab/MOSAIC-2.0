using System;

namespace MOSAIC.Components.Devices.Delsys;

/// <summary>
/// Metadata for a single data channel within a Delsys Trigno sensor.
/// Used for mapping sensor outputs into the unified output vector.
/// </summary>
public sealed class DelsysChannelInfo
{
    /// <summary>Unique GUID of the sensor providing this channel.</summary>
    public Guid SensorId { get; init; }

    /// <summary>Human-readable channel name (e.g. "EMG 1", "Acc X", "Orientation W").</summary>
    public string ChannelName { get; init; } = string.Empty;

    /// <summary>Sensor SID (System Identifier), e.g. 71851.</summary>
    public int SensorSID { get; init; }

    /// <summary>Zero-based index of the sensor within the device source collection.</summary>
    public int DeviceIdx { get; init; }

    /// <summary>Sampling rate of this channel in Hertz.</summary>
    public double SampleRate { get; init; }

    /// <summary>
    /// Number of samples per Trigno frame for this channel.
    /// EMG channels typically have many (e.g. 54); IMU channels usually 1.
    /// </summary>
    public int SamplesPerFrame { get; init; }

    /// <summary>Frame interval in seconds (time span covered by one frame).</summary>
    public double FrameInterval { get; init; }
}