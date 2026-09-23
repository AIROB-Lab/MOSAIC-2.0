using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.Streaming;

namespace MOSAIC.ViewModels.Streaming;

/// <summary>
/// View model for <see cref="UDPControlClient"/>, exposing traffic statistics and live-tunable
/// impedance gains.
/// </summary>
/// <remarks>
/// <see cref="Kp"/> and <see cref="Kd"/> write straight through to the model, so a slider drag is
/// reflected in the very next outgoing packet. No commit step and no restart is required.
/// </remarks>
public partial class UDPControlClientViewModel : ObservableObject, IDisposable
{
    /// <summary>The underlying block. Bound directly for <c>Viz</c>.</summary>
    public UDPControlClient ControlClient { get; set; }

    /// <summary>
    /// Initializes a new <see cref="UDPControlClientViewModel"/> and subscribes to block events.
    /// </summary>
    /// <param name="controlClient">The block to mirror.</param>
    public UDPControlClientViewModel(UDPControlClient controlClient)
    {
        ControlClient = controlClient;

        // Subscribe to model events
        ControlClient.PacketSentEvent += OnPacketSent;
        ControlClient.PacketReceivedEvent += OnPacketReceived;
        ControlClient.StatusChanged += OnStatusChanged;
    }

    // Expose for binding
    public string Name => ControlClient.Name;
    public string Status => ControlClient.Status.ToString();

    public string ConnectionInfo =>
        $"Control: :{ControlClient.ReceivePort} ⇄ {ControlClient.RemoteHost}:{ControlClient.RemotePort}";

    public string ReceivePortText => $":{ControlClient.ReceivePort}";

    public string RemoteEndpointText => $"{ControlClient.RemoteHost}:{ControlClient.RemotePort}";

    public string FormatText => ControlClient.Format.ToString();

    /// <summary>
    /// Human-readable description of the outgoing packet layout, refreshed as packets are sent.
    /// </summary>
    /// <remarks>
    /// Reads as a placeholder until the first packet, because the payload width is only known once
    /// an upstream block has actually delivered a vector.
    /// </remarks>
    public string PacketLayoutText
    {
        get
        {
            var channels = ControlClient.PayloadChannels;
            if (channels < 0) return "awaiting first packet · little-endian";

            var payload = channels == 1 ? "value" : $"ch0…ch{channels - 1}";
            var bytes = (channels + 2) * ControlClient.FieldSize;
            return $"{payload} | kp | kd  ·  {bytes} B  ·  little-endian";
        }
    }

    /// <summary>
    /// Impedance proportional gain. Writes through to the block and takes effect immediately.
    /// </summary>
    public double Kp
    {
        get => ControlClient.Kp;
        set
        {
            if (Math.Abs(ControlClient.Kp - value) < double.Epsilon) return;
            ControlClient.Kp = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Impedance derivative gain. Writes through to the block and takes effect immediately.
    /// </summary>
    public double Kd
    {
        get => ControlClient.Kd;
        set
        {
            if (Math.Abs(ControlClient.Kd - value) < double.Epsilon) return;
            ControlClient.Kd = value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private long _packetsSent;
    [ObservableProperty] private long _packetsReceived;

    private void OnPacketSent(object? sender, PacketEventArgs e)
    {
        PacketsSent = ControlClient.PacketsSent;

        // Cheap enough at control rates, and the layout only ever changes when the payload width does.
        if (_lastReportedBytes != e.ByteCount)
        {
            _lastReportedBytes = e.ByteCount;
            OnPropertyChanged(nameof(PacketLayoutText));
        }
    }

    /// <summary>Byte count of the last packet, used to refresh <see cref="PacketLayoutText"/> only on change.</summary>
    private int _lastReportedBytes = -1;

    private void OnPacketReceived(object? sender, PacketEventArgs e)
    {
        PacketsReceived = ControlClient.PacketsReceived;
    }

    private void OnStatusChanged(object? sender, string status)
    {
        OnPropertyChanged(nameof(Status));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ControlClient.PacketSentEvent -= OnPacketSent;
        ControlClient.PacketReceivedEvent -= OnPacketReceived;
        ControlClient.StatusChanged -= OnStatusChanged;
    }
}
