using MOSAIC.Services;
using MOSAIC.ViewModels.Devices;

namespace MOSAIC.Views.Cards.Devices;

/// <summary>
/// Code-behind for the MCC DAQ card. Drives the ViewModel's live refresh from the shared
/// visualization timer.
/// </summary>
/// <remarks>
/// The tick is owned by the view, not the ViewModel. <c>BlockTemplateSelector</c> builds a fresh
/// <see cref="MccDaqBoardViewModel"/> every time the card is realised and nothing disposes card
/// ViewModels, so a timer owned by the ViewModel would outlive its card and tick forever.
/// Subscribing on attach and unsubscribing on detach keeps the lifetime tied to the visual tree.
/// </remarks>
public partial class MccDaqBoardCardView : PopoutCardBase
{
    /// <summary>Refresh every third 30 fps tick — 10 Hz, enough for liveness and counters.</summary>
    private const int TicksPerRefresh = 3;

    private int _tick;

    public MccDaqBoardCardView()
    {
        InitializeComponent();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        VisualizationTimer.Instance.Subscribe(OnTick, VisualizationTimer.TickRate.Fps30);

        // The clock's rate reaches this block during graph construction, which can land after the
        // card is built; recompute once on attach so the derived scan rate is never stale at the
        // moment the user is deciding whether to press Connect.
        (DataContext as MccDaqBoardViewModel)?.UpdateSummary();
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
        (DataContext as MccDaqBoardViewModel)?.RefreshLive();
    }
}
