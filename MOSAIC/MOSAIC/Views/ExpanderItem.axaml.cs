using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MOSAIC.Components.Basics;
using MOSAIC.Services;
using MOSAIC.Views.Cards;

namespace MOSAIC.Views;

public partial class ExpanderItem : UserControl
{
    private Expander? _rootExpander;

    // The hosted block card, registered when it attaches (i.e. while the row is expanded), so the
    // row's ⚙ settings menu and ⧉ pop-out can act on it. Null while collapsed.
    private PopoutCardBase? _activeCard;
    private Action<PopoutCardBase>? _pendingCardAction;

    /// <summary>
    /// Event raised when the delete button is clicked.
    /// The DataContext (block) should be deleted along with its children.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? DeleteRequested;

    public ExpanderItem()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _rootExpander = this.FindControl<Expander>("RootExpander");
    }

    public bool IsExpanded
    {
        get => _rootExpander?.IsExpanded ?? false;
        set
        {
            if (_rootExpander != null)
                _rootExpander.IsExpanded = value;
        }
    }

    // Template for the nested block card, separate from the UserControl content template.
    public new static readonly StyledProperty<IDataTemplate?> ContentTemplateProperty =
        AvaloniaProperty.Register<ExpanderItem, IDataTemplate?>(nameof(ContentTemplate));

    public new IDataTemplate? ContentTemplate
    {
        get => GetValue(ContentTemplateProperty);
        set => SetValue(ContentTemplateProperty, value);
    }

    private void OnDeleteClicked(object? sender, RoutedEventArgs e)
    {
        // Prevent the click from expanding/collapsing the expander
        e.Handled = true;
        DeleteRequested?.Invoke(this, e);
    }

    /// <summary>Raises <see cref="DeleteRequested"/> — used by the row's ⚙ settings menu.</summary>
    public void RequestDelete() => DeleteRequested?.Invoke(this, new RoutedEventArgs());

    /// <summary>Called by the hosted <see cref="PopoutCardBase"/> when it attaches, so this row's
    /// action buttons can drive it. Runs any action queued while the card wasn't yet realised.</summary>
    public void SetActiveCard(PopoutCardBase card)
    {
        _activeCard = card;
        if (_pendingCardAction is { } action)
        {
            _pendingCardAction = null;
            action(card);
        }
    }

    public void ClearActiveCard(PopoutCardBase card)
    {
        if (ReferenceEquals(_activeCard, card)) _activeCard = null;
    }

    // Runs an action against the hosted card, expanding the row first to realise it if needed.
    private void WithCard(Action<PopoutCardBase> action)
    {
        if (_activeCard is { } card) { action(card); return; }
        _pendingCardAction = action;
        IsExpanded = true;   // realises the card → SetActiveCard runs the queued action
    }

    private void OnPopoutClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!PopoutHelper.IsDesktop) return;
        WithCard(c => c.PopOutCard());
    }

    private void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control anchor) return;

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        // Recording is a per-block switch on every block, not a per-block path: the destination was
        // chosen once in the header, so all this row has to offer is on/off.
        if (DataContext is BaseBlock block)
        {
            var recordItem = new MenuItem
            {
                Header     = block.CanRecord ? "Record to CSV" : "Record to CSV — set a folder first",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked  = block.IsRecording,
                IsEnabled  = block.CanRecord || block.IsRecording
            };
            recordItem.Click += (_, _) =>
            {
                block.IsRecording = !block.IsRecording;

                // Read the tick back off the block: switching on fails when no folder is set, and a
                // menu that showed a tick anyway would claim a recording that does not exist.
                recordItem.IsChecked = block.IsRecording;
            };
            flyout.Items.Add(recordItem);
            flyout.Items.Add(new Separator());
        }

        // "Configure…" only when the realised card actually provides an editor.
        if (_activeCard is { HasConfigView: true })
        {
            var configItem = new MenuItem { Header = "Configure…" };
            configItem.Click += (_, _) => _activeCard?.ShowConfigDialog();
            flyout.Items.Add(configItem);
            flyout.Items.Add(new Separator());
        }

        var deleteItem = new MenuItem { Header = "Delete block" };
        deleteItem.Click += (_, _) => RequestDelete();
        flyout.Items.Add(deleteItem);

        flyout.ShowAt(anchor);
    }
}
