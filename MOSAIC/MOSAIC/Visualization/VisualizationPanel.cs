using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MOSAIC.Visualization.Heatmap;
using MOSAIC.Services;
using MOSAIC.Visualization.ScopeMonitor;
using MOSAIC.Visualization.SpiderMonitor;

namespace MOSAIC.Visualization;

/// <summary>
/// Available visualization types that can be hosted in a <see cref="VisualizationPanel"/>.
/// </summary>
public enum VisualizationType
{
    Scope,
    Spider,
    Heatmap
}

/// <summary>
/// Min/max height range for each visualization slot type.
/// The user drags a slider between these bounds for continuous resizing.
/// </summary>
public readonly record struct HeightRange(double Min, double Max, double Default);

/// <summary>
/// Reusable panel that hosts one or more visualization monitors (Scope, Spider, Heatmap)
/// with individual toggle buttons and a continuous size slider. Each visualization can be
/// independently shown/hidden; hidden monitors are paused to save resources.
///
/// <para>
/// When this panel is detached from the visual tree (e.g. scrolled off-screen),
/// it notifies the bound <see cref="BlockVisualization"/> via
/// <see cref="BlockVisualization.NotifyDetached"/> so that <see cref="BlockVisualization.Feed"/>
/// calls are suppressed. This prevents LiveCharts2 <c>VectorManager</c> crashes that occur
/// when series data is mutated while the chart is not attached to a render context.
/// On re-attachment, <see cref="BlockVisualization.NotifyAttached"/> is called and
/// all visible slots have their views rebuilt and monitors resumed.
/// </para>
///
/// Usage: bind the optional Scope / Spider / Heatmap properties to the block's ViewModel.
/// Only monitors that are bound (non-null) will show a toggle button.
/// </summary>
public class VisualizationPanel : UserControl
{
    #region Styled properties

    public static readonly StyledProperty<BlockVisualization?> SourceProperty =
        AvaloniaProperty.Register<VisualizationPanel, BlockVisualization?>(nameof(Source));

    /// <summary>
    /// Shortcut: bind a <see cref="BlockVisualization"/> and all three monitors
    /// (Scope, Spider, Heatmap) are wired automatically.
    /// If set, individual Scope/Spider/Heatmap properties are ignored.
    /// </summary>
    public BlockVisualization? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly StyledProperty<ScopeMonitor.ScopeMonitor?> ScopeProperty =
        AvaloniaProperty.Register<VisualizationPanel, ScopeMonitor.ScopeMonitor?>(nameof(Scope));

    public ScopeMonitor.ScopeMonitor? Scope
    {
        get => GetValue(ScopeProperty);
        set => SetValue(ScopeProperty, value);
    }

    public static readonly StyledProperty<SpiderMonitor.SpiderMonitor?> SpiderProperty =
        AvaloniaProperty.Register<VisualizationPanel, SpiderMonitor.SpiderMonitor?>(nameof(Spider));

    public SpiderMonitor.SpiderMonitor? Spider
    {
        get => GetValue(SpiderProperty);
        set => SetValue(SpiderProperty, value);
    }

    public static readonly StyledProperty<HeatMapMonitor?> HeatmapProperty =
        AvaloniaProperty.Register<VisualizationPanel, HeatMapMonitor?>(nameof(Heatmap));

    public HeatMapMonitor? Heatmap
    {
        get => GetValue(HeatmapProperty);
        set => SetValue(HeatmapProperty, value);
    }

    public static readonly StyledProperty<bool> ScopeVisibleByDefaultProperty =
        AvaloniaProperty.Register<VisualizationPanel, bool>(nameof(ScopeVisibleByDefault), true);

    public bool ScopeVisibleByDefault
    {
        get => GetValue(ScopeVisibleByDefaultProperty);
        set => SetValue(ScopeVisibleByDefaultProperty, value);
    }

    public static readonly StyledProperty<bool> SpiderVisibleByDefaultProperty =
        AvaloniaProperty.Register<VisualizationPanel, bool>(nameof(SpiderVisibleByDefault), false);

    public bool SpiderVisibleByDefault
    {
        get => GetValue(SpiderVisibleByDefaultProperty);
        set => SetValue(SpiderVisibleByDefaultProperty, value);
    }

    public static readonly StyledProperty<bool> HeatmapVisibleByDefaultProperty =
        AvaloniaProperty.Register<VisualizationPanel, bool>(nameof(HeatmapVisibleByDefault), false);

    public bool HeatmapVisibleByDefault
    {
        get => GetValue(HeatmapVisibleByDefaultProperty);
        set => SetValue(HeatmapVisibleByDefaultProperty, value);
    }

    public static readonly StyledProperty<bool> ShowScopeViewModeToggleProperty =
        AvaloniaProperty.Register<VisualizationPanel, bool>(nameof(ShowScopeViewModeToggle), true);

    public bool ShowScopeViewModeToggle
    {
        get => GetValue(ShowScopeViewModeToggleProperty);
        set => SetValue(ShowScopeViewModeToggleProperty, value);
    }

    #endregion

    #region Fields

    private static readonly Dictionary<VisualizationType, HeightRange> HeightRanges = new()
    {
        [VisualizationType.Scope]   = new(100, 600, 220),
        [VisualizationType.Spider]  = new(150, 700, 320),
        [VisualizationType.Heatmap] = new(120, 650, 280),
    };

    private static readonly ControlTheme ToggleTheme = BuildToggleTheme();

    private readonly Dictionary<VisualizationType, SlotState> _slots = new();
    private StackPanel? _toggleBar;
    private StackPanel? _contentStack;
    private bool _themeSubscribed;
    private bool _isAttached;
    private bool _resolvingSource;
    private BlockVisualization? _attachedSource;

    private sealed class SlotState
    {
        public StackPanel ToggleGroup = null!;
        public ToggleButton Toggle = null!;
        public Border Wrapper = null!;
        public Control? View;
        public bool IsVisible;
        public Control? SizeSlider;
    }

    #endregion

    #region Construction & lifecycle

    public VisualizationPanel()
    {
        CreateLayout();
        SubscribeTheme();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;

        foreach (var (type, slot) in _slots)
        {
            if (slot.IsVisible)
            {
                if (slot.View != null && slot.Wrapper.Child == null)
                {
                    // Re-attach the existing view — do NOT recreate it.
                    // ScopeMonitorView handles its own AvaPlot lifecycle on attach/detach.
                    slot.Wrapper.Child = slot.View;
                }
                else if (slot.View != null && slot.Wrapper.Child != null)
                {
                    // The deferred removal in OnDetachedFromVisualTree hasn't fired yet
                    // (rapid scroll away then back). The view is still logically parented
                    // in the wrapper, but it DID leave the visual tree when the panel
                    // detached — so ScopeMonitorView.OnDetachedFromVisualTree already
                    // ran (Pause, ClearValue). Now that the panel is re-entering the
                    // visual tree, the view re-enters too, and
                    // ScopeMonitorView.OnAttachedToVisualTree fires automatically and
                    // handles the full AvaPlot creation, AttachPlot, and Resume cycle.
                    // No manual intervention needed.
                }
                else if (slot.View == null)
                {
                    // First-ever creation.
                    UpdateSlotView(type, slot);
                }
            }
        }

        // Notify BlockVisualization after views are back in the tree, deferred to
        // Loaded priority so Feed() only un-gates after ScopeMonitorView.OnAttachedToVisualTree
        // has created the AvaPlot and called Resume.
        AttachSourceWhenReady(Source);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;

        // Gate Feed() calls — no point updating charts that aren't rendered.
        DetachSource();

        // Pause all monitors. Keep slot.View alive — ScopeMonitorView handles its own
        // AvaPlot lifecycle so it can be cleanly re-attached.
        // Wrapper child removal is deferred to Background priority but guarded by
        // _isAttached: if the panel re-attaches before the post fires, _isAttached
        // becomes true and the post is a no-op.
        foreach (var (type, slot) in _slots)
        {
            if (slot.IsVisible)
            {
                PauseMonitor(type);

                Dispatcher.UIThread.Post(() =>
                {
                    // Only remove if we are still detached. If the panel re-attached
                    // before this post fired, leave the view in place.
                    if (!_isAttached)
                    {
                        slot.Wrapper.Child = null;
                        // Do NOT null slot.View — we need it for re-attachment.
                    }
                }, DispatcherPriority.Background);
            }
        }
    }

    #endregion

    #region Layout

    private void CreateLayout()
    {
        _toggleBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Margin = new Thickness(0, 0, 4, 4),
        };

        _contentStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
        };

        // Host the stacked monitors in a ScrollViewer so the full set stays reachable
        // when several are visible or a size slider is cranked past the panel height.
        // Auto visibility means it only scrolls when content overflows the allotted
        // height; in an unconstrained host it sizes to content and defers to the outer scroll.
        var contentScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _contentStack,
        };

        var mainPanel = new DockPanel();
        DockPanel.SetDock(_toggleBar, Dock.Top);
        mainPanel.Children.Add(_toggleBar);
        mainPanel.Children.Add(contentScroller);

        Content = mainPanel;

        // Popout button — only on Desktop (Windows/macOS/Linux)
        if (PopoutHelper.IsDesktop)
        {
            _popoutButton = new Button
            {
                Content = "⧉",
                FontSize = 13,
                Padding = new Thickness(5, 1),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            _popoutButton[!Button.ForegroundProperty] = new DynamicResourceExtension(ThemeHelper.TextDimmedBrush);
            ToolTip.SetTip(_popoutButton, "Open in new window");
            _popoutButton.Click += OnPopoutClicked;

            _toggleBar.Children.Add(_popoutButton);
        }
    }

    #endregion

    #region Popout

    private Button? _popoutButton;
    private Window? _popoutWindow;
    private VisualizationPanel? _popoutPanel;

    private void OnPopoutClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Check before pausing/detaching any inline plots or constructing a Window.
        if (!PopoutHelper.IsDesktop) return;

        // If already popped out, bring existing window to front
        if (_popoutWindow is not null)
        {
            _popoutWindow.Activate();
            return;
        }

        // Pause all monitors in the original panel so they release the charts
        foreach (var (type, slot) in _slots)
        {
            if (slot.IsVisible)
                PauseMonitor(type);
        }

        // Hide the original content (keep toggle bar visible so user sees it's popped out)
        if (_contentStack != null)
            _contentStack.IsVisible = false;

        // Create a NEW panel in the popout window that uses our monitors
        _popoutPanel = new VisualizationPanel
        {
            ScopeVisibleByDefault = true,
            ShowScopeViewModeToggle = ShowScopeViewModeToggle,
        };

        // Wire the same monitors — safe now because original views are paused/hidden
        if (Source is not null) _popoutPanel.Source = Source;
        else
        {
            if (Scope != null) _popoutPanel.Scope = Scope;
            if (Spider != null) _popoutPanel.Spider = Spider;
            if (Heatmap != null) _popoutPanel.Heatmap = Heatmap;
        }

        _popoutWindow = new Window
        {
            Title = "MOSAIC — Visualization",
            Width = 1000,
            Height = 650,
            MinWidth = 500,
            MinHeight = 350,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new Border
            {
                Padding = new Thickness(16),
                Child = _popoutPanel,
            },
        };
        _popoutWindow[!Window.BackgroundProperty] = new DynamicResourceExtension("ThemeCardGradient");

        // Update popout button to show "docked" state
        if (_popoutButton != null)
            _popoutButton.Content = "⮨";

        _popoutWindow.Closed += (_, _) =>
        {
            // Tear down the popout panel
            if (_popoutWindow?.Content is Border b)
                b.Child = null;
            _popoutPanel = null;
            _popoutWindow = null;

            // Restore the original panel
            if (_contentStack != null)
                _contentStack.IsVisible = true;

            // Restore the original panel — views will self-resume on attach
            foreach (var (type, slot) in _slots)
            {
                if (slot.IsVisible)
                    UpdateSlotView(type, slot);
            }

            // Restore button icon
            if (_popoutButton != null)
                _popoutButton.Content = "⧉";
        };

        _popoutWindow.Show();
    }

    #endregion

    #region Property changes

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            DetachSource();
            _resolvingSource = true;
            try
            {
                foreach (var (type, slot) in _slots) RemoveSlot(type, slot);
                _slots.Clear();
                _toggleBar?.Children.Clear();
                if (_popoutButton is not null) _toggleBar?.Children.Add(_popoutButton);
                Scope = null; Spider = null; Heatmap = null;
            }
            finally { _resolvingSource = false; }
            if (Source is not null)
            {
                foreach (var type in new[] { VisualizationType.Scope, VisualizationType.Spider, VisualizationType.Heatmap })
                {
                    var slot = CreateSlot(type);
                    _slots[type] = slot;
                    SetSlotVisible(type, slot, DefaultVisible(type));
                }
                AttachSourceWhenReady(Source);
            }
        }
        else if (change.Property == ScopeProperty)
            OnMonitorChanged(VisualizationType.Scope, change.OldValue, change.NewValue);
        else if (change.Property == SpiderProperty)
            OnMonitorChanged(VisualizationType.Spider, change.OldValue, change.NewValue);
        else if (change.Property == HeatmapProperty)
            OnMonitorChanged(VisualizationType.Heatmap, change.OldValue, change.NewValue);
    }

    private bool DefaultVisible(VisualizationType type) => type switch
    {
        VisualizationType.Scope => ScopeVisibleByDefault,
        VisualizationType.Spider => SpiderVisibleByDefault,
        VisualizationType.Heatmap => HeatmapVisibleByDefault,
        _ => false
    };

    private void EnsureSourceMonitor(VisualizationType type)
    {
        if (Source is not { } source) return;
        _resolvingSource = true;
        try
        {
            switch (type)
            {
                case VisualizationType.Scope: Scope = source.Scope; break;
                case VisualizationType.Spider: Spider = source.Spider; break;
                case VisualizationType.Heatmap: Heatmap = source.Heatmap; break;
            }
        }
        finally { _resolvingSource = false; }
    }

    private void AttachSourceWhenReady(BlockVisualization? source)
    {
        if (source is null || !_isAttached) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isAttached || !ReferenceEquals(Source, source) || ReferenceEquals(_attachedSource, source)) return;
            DetachSource();
            _attachedSource = source;
            source.NotifyAttached();
        }, DispatcherPriority.Loaded);
    }

    private void DetachSource()
    {
        _attachedSource?.NotifyDetached();
        _attachedSource = null;
    }

    private void OnMonitorChanged(VisualizationType type, object? oldMonitor, object? newMonitor)
    {
        if (_resolvingSource) return;
        PauseMonitor(type, oldMonitor);

        if (newMonitor != null)
        {
            if (!_slots.TryGetValue(type, out var slot))
            {
                slot = CreateSlot(type);
                _slots[type] = slot;
            }

            slot.Wrapper.Child = null;
            slot.View = null;
            SetSlotVisible(type, slot, DefaultVisible(type));
        }
        else
        {
            if (_slots.TryGetValue(type, out var slot))
            {
                RemoveSlot(type, slot);
                _slots.Remove(type);
            }
        }
    }

    #endregion

    #region Slot management

    private SlotState CreateSlot(VisualizationType type)
    {
        var toggle = new ToggleButton
        {
            Content = GetToggleLabel(type),
            FontSize = 10,
            Padding = new Thickness(8, 3),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeHelper.GetBrush(ThemeHelper.TextPrimaryBrush),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeHelper.GetBrush(ThemeHelper.BorderBrush) as IBrush,
            CornerRadius = new CornerRadius(4),
        };

        toggle.Theme = ToggleTheme;

        toggle.AddHandler(InputElement.PointerPressedEvent, (_, e) => e.Handled = true,
            Avalonia.Interactivity.RoutingStrategies.Bubble);
        toggle.AddHandler(InputElement.PointerReleasedEvent, (_, e) => e.Handled = true,
            Avalonia.Interactivity.RoutingStrategies.Bubble);

        // Custom compact drag track — Canvas so thumb floats freely over the rail
        var range = HeightRanges[type];
        const double trackWidth = 50;
        const double trackHeight = 3;
        const double thumbSize = 8;
        const double canvasHeight = thumbSize;

        var rail = new Border
        {
            Width = trackWidth,
            Height = trackHeight,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(App.CurrentTheme == AppTheme.Dark
                ? Color.Parse("#404040") : Color.Parse("#C0C0C0")),
        };
        Canvas.SetLeft(rail, 0);
        Canvas.SetTop(rail, (canvasHeight - trackHeight) / 2);

        var thumb = new Border
        {
            Width = thumbSize,
            Height = thumbSize,
            CornerRadius = new CornerRadius(thumbSize / 2),
            Background = new SolidColorBrush(App.CurrentTheme == AppTheme.Dark
                ? Color.Parse("#B0B0B0") : Color.Parse("#606060")),
        };
        Canvas.SetTop(thumb, 0);

        var track = new Canvas
        {
            Width = trackWidth,
            Height = canvasHeight,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Children = { rail, thumb },
        };

        // Helpers
        double tDefault = (range.Default - range.Min) / (range.Max - range.Min);
        Canvas.SetLeft(thumb, tDefault * (trackWidth - thumbSize));

        bool dragging = false;

        double HeightFromX(double x)
        {
            double t = Math.Clamp(x / trackWidth, 0, 1);
            return range.Min + t * (range.Max - range.Min);
        }

        void MoveThumb(double height)
        {
            double t = Math.Clamp((height - range.Min) / (range.Max - range.Min), 0, 1);
            Canvas.SetLeft(thumb, t * (trackWidth - thumbSize));
        }

        var wrapper = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 2),
            Height = range.Default,
        };

        track.PointerPressed += (_, e) =>
        {
            dragging = true;
            e.Pointer.Capture(track);
            var pos = e.GetPosition(track);
            var h = HeightFromX(pos.X);
            wrapper.Height = h;
            MoveThumb(h);
            e.Handled = true;
        };

        track.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var pos = e.GetPosition(track);
            var h = HeightFromX(pos.X);
            wrapper.Height = h;
            MoveThumb(h);
        };

        track.PointerReleased += (_, e) =>
        {
            dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };

        var slot = new SlotState
        {
            Toggle = toggle,
            Wrapper = wrapper,
            IsVisible = false,
            SizeSlider = track,
        };

        var capturedType = type;
        toggle.Click += (_, _) =>
        {
            var isNowChecked = toggle.IsChecked == true;
            SetSlotVisible(capturedType, slot, isNowChecked);
            // Re-apply foreground — Avalonia's ToggleButton template overrides it on state change
            UpdateToggleAppearance(slot);
        };

        var toggleGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children = { toggle, track }
        };
        slot.ToggleGroup = toggleGroup;

        // Thin separator between slot groups
        if (_toggleBar!.Children.Count > 0)
        {
            var sep = new Border
            {
                Width = 1,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0),
            };
            sep[!Border.BackgroundProperty] = new DynamicResourceExtension(ThemeHelper.BorderBrush);
            _toggleBar.Children.Add(sep);
        }

        _toggleBar.Children.Add(toggleGroup);
        _contentStack?.Children.Add(wrapper);

        return slot;
    }

    private void RemoveSlot(VisualizationType type, SlotState slot)
    {
        SetSlotVisible(type, slot, false);
        _toggleBar?.Children.Remove(slot.ToggleGroup);
        _contentStack?.Children.Remove(slot.Wrapper);
        slot.Wrapper.Child = null;
        slot.View = null;
    }

    /// <summary>
    /// Forces all visible slots to recreate their views.
    /// Resume happens automatically when each child view attaches to the visual tree.
    /// </summary>
    public void ForceRefresh()
    {
        foreach (var (type, slot) in _slots)
        {
            if (slot.IsVisible)
                UpdateSlotView(type, slot);
        }
    }

    private void UpdateSlotView(VisualizationType type, SlotState slot)
    {
        EnsureSourceMonitor(type);
        slot.Wrapper.Child = null;
        slot.View = null;

        Control? view = type switch
        {
            VisualizationType.Scope   => CreateScopeView(),
            VisualizationType.Spider  => CreateSpiderView(),
            VisualizationType.Heatmap => CreateHeatmapView(),
            _                         => null
        };

        slot.View = view;
        slot.Wrapper.Child = view;
    }

    private Control CreateScopeView()
    {
        var scopeView = new ScopeMonitorView
        {
            ShowViewModeToggle = ShowScopeViewModeToggle,
        };
        scopeView.Bind(ScopeMonitorView.ScopeProperty, new Binding(nameof(Scope)) { Source = this });
        return scopeView;
    }

    private Control CreateSpiderView()
    {
        var spiderView = new SpiderMonitorView();
        spiderView.Bind(SpiderMonitorView.SpiderProperty, new Binding(nameof(Spider)) { Source = this });
        return spiderView;
    }

    private Control CreateHeatmapView()
    {
        var heatmapView = new HeatMapView();
        heatmapView.Bind(HeatMapView.HeatmapProperty, new Binding(nameof(Heatmap)) { Source = this });
        return heatmapView;
    }

    private void UpdateToggleAppearance(SlotState slot)
    {
        var isDark = App.CurrentTheme == AppTheme.Dark;
        var isOn = slot.Toggle.IsChecked == true;

        var accentBrush = ThemeHelper.GetBrush("ThemeAccentBrush");
        var accentColor = (accentBrush is SolidColorBrush scb) ? scb.Color : Colors.DodgerBlue;

        if (isOn)
        {
            slot.Toggle.Foreground = ThemeHelper.GetBrush(ThemeHelper.TextPrimaryBrush);
            // Active: tinted accent background
            slot.Toggle.Background = new SolidColorBrush(
                isDark ? Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B)
                       : Color.FromArgb(40, accentColor.R, accentColor.G, accentColor.B));
            slot.Toggle.BorderBrush = new SolidColorBrush(accentColor);
        }
        else
        {
            slot.Toggle.Foreground = ThemeHelper.GetBrush(ThemeHelper.TextDimmedBrush) as IBrush;
            slot.Toggle.Background = new SolidColorBrush(Colors.Transparent);
            slot.Toggle.BorderBrush = ThemeHelper.GetBrush(ThemeHelper.BorderBrush) as IBrush;
        }
    }

    /// <summary>
    /// Builds the <see cref="ControlTheme"/> shared by every slot toggle button. The default
    /// Fluent template applies theme- and OS-accent brushes in its <c>:pointerover</c> and
    /// <c>:checked</c> states, which override the colours set in <see cref="UpdateToggleAppearance"/>.
    /// This minimal template binds only to the control's own brushes, so the toggle always renders
    /// the MOSAIC theme colours regardless of the host operating system.
    /// </summary>
    private static ControlTheme BuildToggleTheme()
    {
        var template = new FuncControlTemplate<ToggleButton>((parent, _) => new ContentPresenter
        {
            Name = "PART_ContentPresenter",
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            [!ContentPresenter.ContentProperty]         = parent[!ContentControl.ContentProperty],
            [!ContentPresenter.BackgroundProperty]      = parent[!TemplatedControl.BackgroundProperty],
            [!ContentPresenter.ForegroundProperty]      = parent[!TemplatedControl.ForegroundProperty],
            [!ContentPresenter.BorderBrushProperty]     = parent[!TemplatedControl.BorderBrushProperty],
            [!ContentPresenter.BorderThicknessProperty] = parent[!TemplatedControl.BorderThicknessProperty],
            [!ContentPresenter.CornerRadiusProperty]    = parent[!TemplatedControl.CornerRadiusProperty],
            [!ContentPresenter.PaddingProperty]         = parent[!TemplatedControl.PaddingProperty],
        });

        return new ControlTheme(typeof(ToggleButton))
        {
            Setters = { new Setter(TemplatedControl.TemplateProperty, template) },
        };
    }

    private static string GetToggleLabel(VisualizationType type) => type switch
    {
        VisualizationType.Scope   => "\u2248 Scope",    // ≈
        VisualizationType.Spider  => "\u25C7 Spider",   // ◇
        VisualizationType.Heatmap => "\u2593 Heatmap",  // ▓
        _                         => type.ToString()
    };

    private void SetSlotVisible(VisualizationType type, SlotState slot, bool visible)
    {
        slot.IsVisible = visible;
        slot.Toggle.IsChecked = visible;
        slot.Wrapper.IsVisible = visible;

        // Show/hide size slider alongside the toggle
        if (slot.SizeSlider != null) slot.SizeSlider.IsVisible = visible;

        UpdateToggleAppearance(slot);

        if (visible)
        {
            if (slot.View == null)
            {
                // First-ever creation.
                UpdateSlotView(type, slot);
            }
            else if (slot.Wrapper.Child == null)
            {
                // Re-show existing view — re-attach it to the wrapper.
                // ScopeMonitorView.OnAttachedToVisualTree handles reset + resume.
                slot.Wrapper.Child = slot.View;
            }
            else
            {
                // View never left the tree — the deferred removal from the hide path
                // hasn't fired yet (or was cancelled by slot.IsVisible becoming true).
                // Monitor was paused though, so re-gate and resume.
                ResumeMonitorAfterCancelledDetach(type);
            }
        }
        else
        {
            PauseMonitor(type);

            Dispatcher.UIThread.Post(() =>
            {
                if (!slot.IsVisible)
                {
                    // Remove from wrapper but keep slot.View alive so it can be
                    // re-attached without creating a new AvaPlot unnecessarily.
                    // This is safe as a deferred post because the !slot.IsVisible guard
                    // prevents removal if the user toggled visibility back on before
                    // this post fired.
                    slot.Wrapper.Child = null;
                }
            }, DispatcherPriority.Background);
        }
    }

    private void PauseMonitor(VisualizationType type, object? explicitMonitor = null)
    {
        switch (type)
        {
            case VisualizationType.Scope:
                (explicitMonitor as ScopeMonitor.ScopeMonitor ?? Scope)?.Pause();
                break;
            case VisualizationType.Spider:
                (explicitMonitor as SpiderMonitor.SpiderMonitor ?? Spider)?.Pause();
                break;
            case VisualizationType.Heatmap:
                (explicitMonitor as HeatMapMonitor ?? Heatmap)?.Pause();
                break;
        }
    }

    private void ResumeMonitor(VisualizationType type)
    {
        switch (type)
        {
            case VisualizationType.Scope:
                Scope?.Resume();
                break;
            case VisualizationType.Spider:
                Spider?.Resume();
                break;
            case VisualizationType.Heatmap:
                Heatmap?.Resume();
                break;
        }
    }

    /// <summary>
    /// Resumes a monitor after a rapid detach/re-attach cycle where the deferred
    /// wrapper child removal was cancelled. With ScottPlot, no VectorManager or
    /// chart-ready gating is needed — just resume data flow.
    /// </summary>
    private void ResumeMonitorAfterCancelledDetach(VisualizationType type)
    {
        ResumeMonitor(type);
    }

    #endregion

    #region Theme

    private void SubscribeTheme()
    {
        if (_themeSubscribed) return;
        App.ThemeChanged += OnThemeChanged;
        _themeSubscribed = true;
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var (_, slot) in _slots)
            {
                UpdateToggleAppearance(slot);
            }
        }, DispatcherPriority.Render);
    }

    #endregion
}
