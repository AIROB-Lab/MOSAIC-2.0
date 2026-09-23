using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MOSAIC.Visualization;
using ScottPlot.Avalonia;

namespace MOSAIC.Visualization.ScopeMonitor;

/// <summary>
/// Hosts a ScottPlot <see cref="AvaPlot"/> bound to a <see cref="ScopeMonitor"/>. The shared
/// attach/detach, theme, and rebuild lifecycle lives in <see cref="MonitorViewBase{TMonitor}"/>;
/// this class supplies the ScottPlot surface (created fresh on attach and wired through
/// <see cref="ScopeMonitor.AttachPlot"/>) and the stacked/overlapped view-mode toggle.
/// </summary>
public class ScopeMonitorView : MonitorViewBase<ScopeMonitor>
{
    /// <summary>Identifies the <see cref="Scope"/> styled property.</summary>
    public static readonly StyledProperty<object?> ScopeProperty =
        AvaloniaProperty.Register<ScopeMonitorView, object?>(nameof(Scope));

    /// <summary>The <see cref="ScopeMonitor"/> whose channels this control displays.</summary>
    public object? Scope
    {
        get => GetValue(ScopeProperty);
        set => SetValue(ScopeProperty, value);
    }

    /// <summary>Identifies the <see cref="ShowViewModeToggle"/> styled property.</summary>
    public static readonly StyledProperty<bool> ShowViewModeToggleProperty =
        AvaloniaProperty.Register<ScopeMonitorView, bool>(nameof(ShowViewModeToggle), true);

    /// <summary>Whether the stacked/overlapped view-mode toggle is shown.</summary>
    public bool ShowViewModeToggle
    {
        get => GetValue(ShowViewModeToggleProperty);
        set => SetValue(ShowViewModeToggleProperty, value);
    }

    private AvaPlot? _avaPlot;
    private ToggleSwitch? _viewModeToggle;
    private Border? _chartContainer;
    private bool _suppressToggleHandler;

    private VisualizationSettingsFlyout? _settingsFlyout;
    private Button? _gearButton;

    /// <summary>Creates the view and builds its static layout.</summary>
    public ScopeMonitorView()
    {
        CreateLayout();
    }

    private void CreateLayout()
    {
        _viewModeToggle = new ToggleSwitch
        {
            OnContent = "Stacked",
            OffContent = "Overlapped",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 2),
            IsVisible = ShowViewModeToggle
        };
        _viewModeToggle[!ToggleSwitch.ForegroundProperty] =
            new DynamicResourceExtension(ThemeHelper.TextPrimaryBrush);
        _viewModeToggle.IsCheckedChanged += OnViewModeToggled;
        _viewModeToggle.AddHandler(InputElement.PointerPressedEvent,
            (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        _viewModeToggle.AddHandler(InputElement.PointerReleasedEvent,
            (_, e) => e.Handled = true, RoutingStrategies.Bubble);

        _settingsFlyout = new VisualizationSettingsFlyout(SettingsTarget.Scope);
        _gearButton = VisualizationSettingsFlyout.CreateGearButton(_settingsFlyout);
        _gearButton[!Button.ForegroundProperty] =
            new DynamicResourceExtension(ThemeHelper.TextDimmedBrush);

        var togglePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 4,
            Margin = new Thickness(0, 0, 4, 2),
            Children = { _viewModeToggle, _gearButton }
        };

        _chartContainer = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = new SolidColorBrush(ThemeHelper.ChartBackground),
            MinHeight = 120,
            MinWidth = 200
        };

        var mainPanel = new DockPanel();
        DockPanel.SetDock(togglePanel, Dock.Top);
        mainPanel.Children.Add(togglePanel);
        mainPanel.Children.Add(_chartContainer);
        Content = mainPanel;
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ScopeProperty)
            HandleMonitorChanged(change.NewValue as ScopeMonitor);
        else if (change.Property == ShowViewModeToggleProperty && _viewModeToggle != null)
            _viewModeToggle.IsVisible = ShowViewModeToggle;
    }

    /// <inheritdoc/>
    protected override void OnMonitorAssigned(ScopeMonitor? oldMonitor, ScopeMonitor? newMonitor)
    {
        // The previous monitor owns the live AvaPlot; unwire it before the rebuild swaps surfaces.
        oldMonitor?.DetachPlot();
        _settingsFlyout?.SetScope(newMonitor);
        SyncViewModeToggle();
    }

    /// <inheritdoc/>
    protected override void ApplyTheme()
    {
        if (_chartContainer != null)
            _chartContainer.Background = new SolidColorBrush(ThemeHelper.ChartBackground);
    }

    /// <inheritdoc/>
    protected override void CreateSurface()
    {
        if (Monitor == null || _chartContainer == null) return;

        _avaPlot = new AvaPlot
        {
            MinWidth = 200,
            MinHeight = 120,
        };

        DisableZoom(_avaPlot);
        _chartContainer.Child = _avaPlot;

        Monitor.AttachPlot(_avaPlot.Plot, () => _avaPlot?.Refresh());

        SyncViewModeToggle();
    }

    /// <inheritdoc/>
    protected override void DestroySurface()
    {
        Monitor?.DetachPlot();
        if (_chartContainer != null)
            _chartContainer.Child = null;
        _avaPlot = null;
    }

    private void OnViewModeToggled(object? sender, RoutedEventArgs e)
    {
        if (_suppressToggleHandler) return;
        if (Monitor == null || _viewModeToggle == null) return;

        Monitor.ViewMode = _viewModeToggle.IsChecked == true
            ? ScopeViewMode.Stacked
            : ScopeViewMode.Overlapped;
    }

    private void SyncViewModeToggle()
    {
        if (_viewModeToggle == null || Monitor == null) return;
        _suppressToggleHandler = true;
        _viewModeToggle.IsChecked = Monitor.ViewMode == ScopeViewMode.Stacked;
        _suppressToggleHandler = false;
    }

    /// <summary>
    /// Removes every zoom interaction from the plot's input processor — scroll-wheel zoom,
    /// right-drag zoom, middle-drag zoom-rectangle, and keyboard pan/zoom — while leaving
    /// panning intact. The scope drives its own X/Y limits, so user zoom only fights the
    /// live auto-scaling.
    /// </summary>
    private static void DisableZoom(AvaPlot plot)
    {
        var proc = plot.UserInputProcessor;
        proc.RemoveAll<ScottPlot.Interactivity.UserActionResponses.MouseWheelZoom>();
        proc.RemoveAll<ScottPlot.Interactivity.UserActionResponses.MouseDragZoom>();
        proc.RemoveAll<ScottPlot.Interactivity.UserActionResponses.MouseDragZoomRectangle>();
        proc.RemoveAll<ScottPlot.Interactivity.UserActionResponses.KeyboardPanAndZoom>();
    }
}