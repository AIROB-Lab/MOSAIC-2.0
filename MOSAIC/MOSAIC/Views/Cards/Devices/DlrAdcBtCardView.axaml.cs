using Avalonia.Controls;
using MOSAIC.Services;
using MOSAIC.ViewModels.Devices;

namespace MOSAIC.Views.Cards.Devices;

/// <summary>
/// Code-behind for the DLR ADC/BT card. Drives the ViewModel's live refresh from the shared
/// visualization timer.
/// </summary>
/// <remarks>
/// The tick is owned by the view, not the ViewModel. <c>BlockTemplateSelector</c> builds a fresh
/// <see cref="DlrAdcBtViewModel"/> every time the card is realised and nothing disposes card
/// ViewModels, so a timer owned by the ViewModel would outlive its card and tick forever.
/// Subscribing on attach and unsubscribing on detach keeps the lifetime tied to the visual tree.
/// </remarks>
public partial class DlrAdcBtCardView : PopoutCardBase
{
    /// <summary>Refresh every third 30 fps tick — 10 Hz, enough for liveness and channel readouts.</summary>
    private const int TicksPerRefresh = 3;

    private int _tick;

    public DlrAdcBtCardView()
    {
        InitializeComponent();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        VisualizationTimer.Instance.Subscribe(OnTick, VisualizationTimer.TickRate.Fps30);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        VisualizationTimer.Instance.Unsubscribe(OnTick);
    }

    private void OnTick()
    {
        if (++_tick % TicksPerRefresh != 0) return;
        (DataContext as DlrAdcBtViewModel)?.RefreshLive();
    }
}
