using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.Avalonia;
using MOSAIC.Visualization;

namespace MOSAIC.Visualization.Heatmap;

/// <summary>
/// Hosts a LiveCharts heatmap (Cartesian chart) with a colorbar legend, bound to a
/// <see cref="HeatMapMonitor"/>. The shared attach/detach, theme, and rebuild lifecycle lives in
/// <see cref="MonitorViewBase{TMonitor}"/>; this class supplies the chart surface, the
/// colorbar bindings, and an optional height drag-track.
/// </summary>
public class HeatMapView : MonitorViewBase<HeatMapMonitor>
{
    public static readonly StyledProperty<object?> HeatmapProperty =
        AvaloniaProperty.Register<HeatMapView, object?>(nameof(Heatmap));

    public object? Heatmap
    {
        get => GetValue(HeatmapProperty);
        set => SetValue(HeatmapProperty, value);
    }

    public static readonly StyledProperty<bool> ShowSizeTrackProperty =
        AvaloniaProperty.Register<HeatMapView, bool>(nameof(ShowSizeTrack), false);

    /// <summary>
    /// Shows the drag-track height slider above the chart.
    /// Set to <see langword="true"/> when using HeatMapView standalone (outside VisualizationPanel).
    /// Defaults to <see langword="false"/> since VisualizationPanel has its own sizing control.
    /// </summary>
    public bool ShowSizeTrack
    {
        get => GetValue(ShowSizeTrackProperty);
        set => SetValue(ShowSizeTrackProperty, value);
    }

    private CartesianChart? _chart;
    private Border? _chartContainer;
    private Grid? _legendGrid;
    private Image? _colorbarImage;
    private TextBlock? _maxLabel;
    private TextBlock? _minLabel;
    private TextBlock? _captionLabel;
    private Button? _gearButton;
    private VisualizationSettingsFlyout? _settingsFlyout;
    private readonly object _chartLock = new();

    // Size drag track
    private Canvas? _sizeTrack;
    private Border? _sizeThumb;
    private bool _dragging;
    private static readonly HeightRange Heights = new(120, 650, 280);

    /// <summary>Creates the view and builds its static layout.</summary>
    public HeatMapView()
    {
        CreateLayout();
    }

    /// <inheritdoc/>
    protected override void ApplyTheme()
    {
        var bg = new SolidColorBrush(ThemeHelper.ChartBackground);
        if (_chartContainer != null) _chartContainer.Background = bg;
        if (_chart != null) _chart.Background = bg;
        if (_legendGrid != null) _legendGrid.Background = bg;
        if (Content is Border root) root.Background = bg;
    }

    // ---------------------------
    // Layout
    // ---------------------------

    private void CreateLayout()
    {
        const double legendWidth = 64;

        // ── Gear button with settings flyout ──
        _settingsFlyout = new VisualizationSettingsFlyout(SettingsTarget.Heatmap);
        _gearButton = VisualizationSettingsFlyout.CreateGearButton(_settingsFlyout);
        _gearButton[!Button.ForegroundProperty] = new DynamicResourceExtension(ThemeHelper.TextDimmedBrush);

        _colorbarImage = new Image
        {
            Width = 20,
            Stretch = Stretch.Fill,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 2, 8, 2)
        };

        _maxLabel = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2),
            MaxWidth = legendWidth - 16,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _maxLabel[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(ThemeHelper.TextPrimaryBrush);

        _minLabel = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            MaxWidth = legendWidth - 16,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _minLabel[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(ThemeHelper.TextPrimaryBrush);

        _captionLabel = new TextBlock
        {
            FontSize = 10,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(4, 4, 4, 0),
            MaxWidth = legendWidth - 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _captionLabel[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(ThemeHelper.TextDimmedBrush);

        // Legend layout: ⚙ gear | max label | colorbar (stretch) | min label | caption
        _legendGrid = new Grid
        {
            Width = legendWidth,
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*,Auto,Auto"),
            ClipToBounds = true,
            Background = new SolidColorBrush(ThemeHelper.ChartBackground),
        };
        Grid.SetRow(_gearButton, 0);     _legendGrid.Children.Add(_gearButton);
        Grid.SetRow(_maxLabel, 1);       _legendGrid.Children.Add(_maxLabel);
        Grid.SetRow(_colorbarImage, 2);  _legendGrid.Children.Add(_colorbarImage);
        Grid.SetRow(_minLabel, 3);       _legendGrid.Children.Add(_minLabel);
        Grid.SetRow(_captionLabel, 4);   _legendGrid.Children.Add(_captionLabel);

        _chartContainer = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = false,
            Background = new SolidColorBrush(ThemeHelper.ChartBackground),
            MinHeight = Heights.Min,
            MinWidth = 200,
        };

        var contentGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ClipToBounds = true,
            Height = Heights.Default,
        };
        Grid.SetColumn(_chartContainer, 0); contentGrid.Children.Add(_chartContainer);
        Grid.SetColumn(_legendGrid, 1);     contentGrid.Children.Add(_legendGrid);

        // ── Custom compact drag track (Canvas: 50×8px rail + 8px thumb) ──
        const double trackWidth = 50;
        const double trackHeight = 3;
        const double thumbSize = 8;
        const double canvasHeight = thumbSize;

        var isDark = App.CurrentTheme == AppTheme.Dark;

        var rail = new Border
        {
            Width = trackWidth,
            Height = trackHeight,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(isDark
                ? Color.Parse("#404040") : Color.Parse("#C0C0C0")),
        };
        Canvas.SetLeft(rail, 0);
        Canvas.SetTop(rail, (canvasHeight - trackHeight) / 2);

        _sizeThumb = new Border
        {
            Width = thumbSize,
            Height = thumbSize,
            CornerRadius = new CornerRadius(thumbSize / 2),
            Background = new SolidColorBrush(isDark
                ? Color.Parse("#B0B0B0") : Color.Parse("#606060")),
        };
        Canvas.SetTop(_sizeThumb, 0);

        double tDefault = (Heights.Default - Heights.Min) / (Heights.Max - Heights.Min);
        Canvas.SetLeft(_sizeThumb, tDefault * (trackWidth - thumbSize));

        _sizeTrack = new Canvas
        {
            Width = trackWidth,
            Height = canvasHeight,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 6, 2),
            Cursor = new Cursor(StandardCursorType.Hand),
            Children = { rail, _sizeThumb },
            IsVisible = ShowSizeTrack,
        };

        _sizeTrack.PointerPressed += (_, e) =>
        {
            _dragging = true;
            e.Pointer.Capture(_sizeTrack);
            ApplyTrackPosition(e.GetPosition(_sizeTrack).X, trackWidth, thumbSize, contentGrid);
            e.Handled = true;
        };

        _sizeTrack.PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            ApplyTrackPosition(e.GetPosition(_sizeTrack).X, trackWidth, thumbSize, contentGrid);
        };

        _sizeTrack.PointerReleased += (_, e) =>
        {
            _dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };

        var mainPanel = new DockPanel();
        DockPanel.SetDock(_sizeTrack, Dock.Top);
        mainPanel.Children.Add(_sizeTrack);
        mainPanel.Children.Add(contentGrid);

        var root = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(ThemeHelper.ChartBackground),
            Child = mainPanel
        };

        Content = root;
    }

    private void ApplyTrackPosition(double x, double trackWidth, double thumbSize, Grid target)
    {
        double t = Math.Clamp(x / trackWidth, 0, 1);
        double newHeight = Heights.Min + t * (Heights.Max - Heights.Min);

        target.Height = newHeight;

        if (_sizeThumb != null)
            Canvas.SetLeft(_sizeThumb, t * (trackWidth - thumbSize));
    }

    // ---------------------------
    // Chart lifecycle
    // ---------------------------

    /// <inheritdoc/>
    protected override void CreateSurface()
    {
        lock (_chartLock)
        {
            if (_chart != null || _chartContainer == null || Monitor == null) return;

            _chart = new CartesianChart
            {
                MinWidth = 200,
                MinHeight = 200,
                TooltipPosition = TooltipPosition.Hidden,
                EasingFunction = null,
                AnimationsSpeed = TimeSpan.Zero,
                Background = new SolidColorBrush(ThemeHelper.ChartBackground),
                Padding = new Thickness(12),
            };

            _chart.Bind(CartesianChart.SeriesProperty, new Binding("Heatmap.Series") { Source = this });
            _chart.Bind(CartesianChart.XAxesProperty, new Binding("Heatmap.XAxes") { Source = this });
            _chart.Bind(CartesianChart.YAxesProperty, new Binding("Heatmap.YAxes") { Source = this });
            _chart.Bind(CartesianChart.SyncContextProperty, new Binding("Heatmap.SyncContext") { Source = this });

            // Signal the monitor that its chart surface is ready to receive data.
            var heatmap = Monitor;
            _chart.AttachedToVisualTree += (_, _) =>
                Dispatcher.UIThread.Post(() => heatmap?.NotifyChartReady(), DispatcherPriority.Loaded);

            _chartContainer.Child = _chart;

            // Bind legend
            _colorbarImage?.Bind(Image.SourceProperty, new Binding("Heatmap.ColorbarBitmap") { Source = this });
            _maxLabel?.Bind(TextBlock.TextProperty, new Binding("Heatmap.ColorbarMaxLabel") { Source = this });
            _minLabel?.Bind(TextBlock.TextProperty, new Binding("Heatmap.ColorbarMinLabel") { Source = this });
            _captionLabel?.Bind(TextBlock.TextProperty, new Binding("Heatmap.ColorbarCaption") { Source = this });
        }
    }

    /// <inheritdoc/>
    protected override void DestroySurface()
    {
        lock (_chartLock)
        {
            if (_chart == null) return;

            try
            {
                _chart.ClearValue(CartesianChart.SeriesProperty);
                _chart.ClearValue(CartesianChart.XAxesProperty);
                _chart.ClearValue(CartesianChart.YAxesProperty);
                _chart.ClearValue(CartesianChart.SyncContextProperty);

                _chart.Series = Array.Empty<LiveChartsCore.ISeries>();
                _chart.XAxes = Array.Empty<LiveChartsCore.SkiaSharpView.Axis>();
                _chart.YAxes = Array.Empty<LiveChartsCore.SkiaSharpView.Axis>();
                _chart.SyncContext = null;

                if (_chartContainer != null)
                    _chartContainer.Child = null;

                _colorbarImage?.ClearValue(Image.SourceProperty);
                _maxLabel?.ClearValue(TextBlock.TextProperty);
                _minLabel?.ClearValue(TextBlock.TextProperty);
                _captionLabel?.ClearValue(TextBlock.TextProperty);
            }
            catch { /* swallow */ }
            finally
            {
                _chart = null;
            }
        }
    }

    // ---------------------------
    // Property changes
    // ---------------------------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == HeatmapProperty)
            HandleMonitorChanged(change.NewValue as HeatMapMonitor);
        else if (change.Property == ShowSizeTrackProperty && _sizeTrack != null)
            _sizeTrack.IsVisible = ShowSizeTrack;
    }

    /// <inheritdoc/>
    protected override void OnMonitorAssigned(HeatMapMonitor? oldMonitor, HeatMapMonitor? newMonitor)
    {
        _settingsFlyout?.SetHeatmap(newMonitor);
        newMonitor?.RebuildColorbar();
    }
}