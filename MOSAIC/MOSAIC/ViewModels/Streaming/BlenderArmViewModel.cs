using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.Streaming;
using MOSAIC.Visualization;

namespace MOSAIC.ViewModels.Streaming;

/// <summary>
/// ViewModel for <see cref="BlenderArm"/>.
/// Wires visualization and exposes connection commands.
/// </summary>
public partial class BlenderArmViewModel : ObservableObject
{
    private readonly BlenderArm _blenderArm;

    /// <summary>Direct access to the model for XAML bindings.</summary>
    public BlenderArm BlenderArm => _blenderArm;

    /// <summary>Visualization bundle for the outgoing command vector.</summary>
    public BlockVisualization Viz { get; } = new();

    public BlenderArmViewModel(BlenderArm blenderArm)
    {
        _blenderArm = blenderArm;
        _blenderArm.Viz = Viz;
    }

    [RelayCommand]
    private void Connect() => _blenderArm.Connect();

    [RelayCommand]
    private void Disconnect() => _blenderArm.Disconnect();

    [RelayCommand]
    private void Reconnect() => _blenderArm.Reconnect();
}