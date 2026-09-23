using MOSAIC.Models.Streaming;
using MOSAIC.Visualization;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;


namespace MOSAIC.ViewModels.Streaming;


/// <summary>
/// ViewModel for <see cref="UdpStreamlinedSender"/>.
/// Wires visualization, exposes connection commands and protocol reference.
/// </summary>
public partial class UdpStreamlinedSenderViewModel : ObservableObject
{
    private readonly UdpStreamlinedSender _sender;

    /// <summary>Direct access to the model (for XAML bindings like <c>{Binding Sender.HostIp}</c>).</summary>
    public UdpStreamlinedSender Sender => _sender;

    /// <summary>Visualization bundle for the outgoing data stream.</summary>
    public BlockVisualization Viz { get; } = new();

    /// <summary>Protocol reference entries for the UI table.</summary>
    public IReadOnlyList<ProtocolEntry> ProtocolEntries { get; }

    /// <summary>Whether the protocol reference expander is open.</summary>
    [ObservableProperty] private bool _isProtocolExpanded;

    /// <summary>Whether the message log expander is open.</summary>
    [ObservableProperty] private bool _isLogExpanded;

    public UdpStreamlinedSenderViewModel(UdpStreamlinedSender sender)
    {
        _sender = sender;
        _sender.Viz = Viz;

        // Build protocol reference from DataType helper
        var entries = new List<ProtocolEntry>();
        foreach (var (cat, sub, label) in DataType.GetAllEntries())
        {
            entries.Add(new ProtocolEntry(cat, sub, label));
        }
        ProtocolEntries = entries;
    }

    [RelayCommand]
    private void Connect() => _sender.Connect();

    [RelayCommand]
    private void Disconnect() => _sender.Disconnect();

    [RelayCommand]
    private void Reconnect() => _sender.Reconnect();

    [RelayCommand]
    private void ClearLog() => _sender.MessageLog.Clear();
}

/// <summary>
/// A single row in the protocol reference table.
/// </summary>
public record ProtocolEntry(byte Category, byte Subtype, string Label)
{
    /// <summary>Display string for the type code column, e.g. "[1.3]".</summary>
    public string Code => $"[{Category}.{Subtype}]";
}