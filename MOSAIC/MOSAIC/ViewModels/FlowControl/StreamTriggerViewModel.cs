using System;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.ViewModels.FlowControl;

/// <summary>
/// ViewModel for <see cref="StreamTrigger"/>.
/// </summary>
/// <remarks>
/// <para>
/// Thin pass-through: the block already exposes its state as <c>[ObservableProperty]</c>
/// and its actions as <c>[RelayCommand]</c>, so the VM just forwards the model reference
/// for direct binding and rebroadcasts property-changed notifications onto the UI
/// thread. No additional state of its own.
/// </para>
/// <para>
/// Bindings in the AXAML target <c>StreamTrigger.IsStreaming</c>, <c>StreamTrigger.SessionName</c>,
/// <c>StreamTrigger.LatestLabelText</c>, <c>StreamTrigger.StatusText</c>, <c>StreamTrigger.PublishedCount</c>,
/// and <c>StreamTrigger.ToggleStreamCommand</c> directly via <c>{Binding StreamTrigger.X}</c>.
/// </para>
/// </remarks>
public partial class StreamTriggerViewModel : ObservableObject, IDisposable
{
    /// <summary>The underlying block — AXAML binds directly to its properties and commands.</summary>
    public StreamTrigger StreamTrigger { get; }

    public StreamTriggerViewModel(StreamTrigger block)
    {
        StreamTrigger = block ?? throw new ArgumentNullException(nameof(block));

        // Forward block property changes to the UI thread. Block's OnReceive runs
        // on a background thread, so PropertyChanged fired from [ObservableProperty]
        // setters during data arrival lands there too — we marshal it back so any
        // bindings update on the right thread.
        StreamTrigger.PropertyChanged += OnBlockPropertyChanged;
    }

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The block itself owns the observable state; we just re-raise on the UI
        // thread so AXAML bindings refresh cleanly even when the source change
        // happened off-thread.
        Dispatcher.UIThread.Post(() => OnPropertyChanged(e.PropertyName),
                                 DispatcherPriority.Background);
    }

    public void Dispose()
    {
        StreamTrigger.PropertyChanged -= OnBlockPropertyChanged;
    }
}