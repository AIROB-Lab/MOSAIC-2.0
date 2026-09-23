using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using MOSAIC.Visualization;
using MOSAIC.Services;

namespace MOSAIC.Views.Cards;

/// <summary>
/// Base class for card views that adds a "pop out to new window" button on desktop.
///
/// Click ⧉ → original card is hidden, a fresh copy opens in a new window with
/// the same DataContext. Only the popout is active — no monitor conflicts.
/// Click ⮨ or close window → popout is destroyed, original card reappears.
///
/// Usage — change the base class in code-behind:
///   public partial class MyCardView : PopoutCardBase { }
///
/// No XAML changes needed. On mobile/browser, nothing happens.
/// </summary>
public class PopoutCardBase : UserControl
{
    private Window? _popoutWindow;
    private bool _isPoppedOut;
    private ExpanderItem? _owner;   // the block-list row hosting this card
    internal MOSAIC.Components.Basics.BaseBlock? OwnerBlock { get; set; }

    /// <summary>
    /// Override to provide this card's configuration editor. When non-null, the block-list row's ⚙
    /// menu gains a "Configure…" item that opens the view in a <see cref="BlockConfigDialog"/>.
    /// Return a FRESH instance each call (it's hosted in a dialog and discarded on close).
    /// </summary>
    protected virtual Control? CreateConfigView() => null;

    /// <summary>True when this card supplies a configuration editor (see <see cref="CreateConfigView"/>).</summary>
    public bool HasConfigView => CreateConfigView() is not null;

    /// <summary>Opens this card's configuration editor in a modal dialog (no-op if it has none).</summary>
    public void ShowConfigDialog()
    {
        if (CreateConfigView() is { } body)
            BlockConfigDialog.Show(this, DataContext, body);
    }

    /// <summary>Pops this card out into its own window (no-op if already popped out).</summary>
    public void PopOutCard()
    {
        if (!PopoutHelper.IsDesktop) return;
        if (_popoutWindow == null) PopOut();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!PopoutHelper.IsDesktop || _isPoppedOut) return;

        // Register with the hosting block-list row so its ⚙ settings menu and ⧉ pop-out button —
        // which sit in the always-visible row header, clear of the card's own status badge and
        // rounded corners — can act on this card.
        _owner = this.FindAncestorOfType<ExpanderItem>();
        _owner?.SetActiveCard(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _owner?.ClearActiveCard(this);
        _owner = null;
    }


    private void PopOut()
    {
        // Create a fresh instance of the same card type
        Control popoutCard;
        try
        {
            popoutCard = (Control)Activator.CreateInstance(this.GetType())!;
        }
        catch
        {
            return;
        }
        
        if (popoutCard is PopoutCardBase popoutBase)
            popoutBase._isPoppedOut = true;
        
        // Share the same DataContext
        popoutCard.DataContext = this.DataContext;

        // HIDE the original so only the popout's monitors are active
        this.IsVisible = false;

        var boundsWidth = this.Bounds.Width;

        // Build a return button for the popout window header
        var returnButton = new Button
        {
            Content = "Return to main",
            FontSize = 11,
            Padding = new Thickness(10, 5),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(16, 8, 0, 0),
        };
        returnButton[!Button.ForegroundProperty] = new DynamicResourceExtension(ThemeHelper.TextSecondaryBrush);
        returnButton.Click += (_, _) => _popoutWindow?.Close();

        var windowContent = new DockPanel();
        DockPanel.SetDock(returnButton, Dock.Top);
        windowContent.Children.Add(returnButton);

        var scroll = new ScrollViewer
        {
            Content = popoutCard,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        windowContent.Children.Add(scroll);

        _popoutWindow = new Window
        {
            Title = "MOSAIC",
            Width = Math.Max(700, boundsWidth + 40),
            Height = 850,
            MinWidth = 450,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = windowContent,
        };
        _popoutWindow[!Window.BackgroundProperty] = new DynamicResourceExtension("ThemeBackgroundBrush");

        _popoutWindow.Closed += (_, _) =>
        {
            if (OwnerBlock is { } block) block.Disposing -= OnBlockDisposing;
            // Tear down popout
            scroll.Content = null;
            windowContent.Children.Clear();
            _popoutWindow = null;

            // Show original card again
            this.IsVisible = true;

            // Force all VisualizationPanels to rebuild their views.
            // When IsVisible was false, the views detached and paused.
            // Just toggling visibility back doesn't fully restart them.
            RefreshVisualizationPanels(this);
        };

        if (OwnerBlock is { } ownerBlock) ownerBlock.Disposing += OnBlockDisposing;
        _popoutWindow.Show();
    }

    private void OnBlockDisposing(object? sender, EventArgs e) => _popoutWindow?.Close();

    /// <summary>
    /// Walks the visual tree and calls ForceRefresh on every VisualizationPanel found.
    /// </summary>
    private static void RefreshVisualizationPanels(Control root)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            FindAndRefreshPanels(root);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private static void FindAndRefreshPanels(Control control)
    {
        if (control is VisualizationPanel panel)
        {
            panel.ForceRefresh();
        }

        foreach (var child in control.GetVisualChildren())
        {
            if (child is Control childControl)
                FindAndRefreshPanels(childControl);
        }
    }
}
