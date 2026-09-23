using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.Streaming;

namespace MOSAIC.ViewModels.Streaming;

public partial class UDPClientViewModel : ObservableObject, IDisposable
{
    public UDPClient UdpClient { get; set; }

    public UDPClientViewModel(UDPClient udpClient)
    {
        UdpClient = udpClient;
        
        _receivePortText = $":{UdpClient.ReceivePort}";
        _remoteEndpointText = UdpClient.RemoteHost != null
            ? $"{UdpClient.RemoteHost}:{UdpClient.RemotePort}"
            : "Not configured";
        
        // Subscribe to model events
        UdpClient.PacketSentEvent += OnPacketSent;
        UdpClient.PacketReceivedEvent += OnPacketReceived;
        UdpClient.StatusChanged += OnStatusChanged;
    }

    // Expose for binding
    public string Name => UdpClient.Name;
    public string Status => UdpClient.Status.ToString();

    public string ConnectionInfo => UdpClient.RemoteHost != null 
        ? $"Bidirectional: :{UdpClient.ReceivePort} ⇄ {UdpClient.RemoteHost}:{UdpClient.RemotePort}"
        : $"Receive only: :{UdpClient.ReceivePort}";

    [ObservableProperty]
    private string _receivePortText;

    /// <summary>Parses ":PORT" format and writes back to the model.</summary>
    partial void OnReceivePortTextChanged(string value)
    {
        var raw = value.TrimStart(':');
        if (int.TryParse(raw, out int port) && port is > 0 and <= 65535)
        {
            UdpClient.ReceivePort = port;
            OnPropertyChanged(nameof(ConnectionInfo));
        }
    }

    [ObservableProperty]
    private string _remoteEndpointText;

    /// <summary>Parses "HOST:PORT" format and writes back to the model.</summary>
    partial void OnRemoteEndpointTextChanged(string value)
    {
        // Empty or user typed nothing → reset to "Not configured"
        if (string.IsNullOrWhiteSpace(value) || value == "Not configured")
        {
            UdpClient.RemoteHost = null;
            OnPropertyChanged(nameof(ConnectionInfo));
            // Write the label back so the TextBox shows it
            RemoteEndpointText = "Not configured";
            return;
        }

        var lastColon = value.LastIndexOf(':');
        if (lastColon > 0
            && int.TryParse(value[(lastColon + 1)..], out int port)
            && port is > 0 and <= 65535)
        {
            UdpClient.RemoteHost = value[..lastColon];
            UdpClient.RemotePort = port;
            OnPropertyChanged(nameof(ConnectionInfo));
        }
        // If parse fails, do nothing — user is still typing
    }


    public string FormatText => UdpClient.Format.ToString();

    [ObservableProperty] private long _packetsSent;
    [ObservableProperty] private long _packetsReceived;

    private void OnPacketSent(object? sender, PacketEventArgs e)
    {
        PacketsSent = UdpClient.PacketsSent;
    }

    private void OnPacketReceived(object? sender, PacketEventArgs e)
    {
        PacketsReceived = UdpClient.PacketsReceived;
    }

    private void OnStatusChanged(object? sender, string status)
    {
        OnPropertyChanged(nameof(Status));
    }

    public void Dispose()
    {
        UdpClient.PacketSentEvent -= OnPacketSent;
        UdpClient.PacketReceivedEvent -= OnPacketReceived;
        UdpClient.StatusChanged -= OnStatusChanged;
    }
}