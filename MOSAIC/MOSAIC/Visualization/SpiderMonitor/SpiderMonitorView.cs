using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using LiveChartsCore.SkiaSharpView.Avalonia;
using LiveChartsGeneratedCode;

namespace MOSAIC.Visualization.SpiderMonitor;

/// <summary>
/// Hosts a LiveCharts polar ("spider"/radar) chart bound to a <see cref="SpiderMonitor"/>.
/// The shared attach/detach, theme, and rebuild lifecycle lives in
/// <see cref="MonitorViewBase{TMonitor}"/>; this class supplies only the polar-chart surface
/// and its bindings.
/// </summary>
public class SpiderMonitorView : MonitorViewBase<SpiderMonitor>
{
    #region Styled property

    /// <summary>Identifies the <see cref="Spider"/> styled property.</summary>
    public static readonly StyledProperty<object?> SpiderProperty =
        AvaloniaProperty.Register<SpiderMonitorView, object?>(nameof(Spider));

    /// <summary>
    /// The <see cref="SpiderMonitor"/> whose series and axes this control displays.
    /// </summary>
    public object? Spider
    {
        get => GetValue(SpiderProperty);
        set => SetValue(SpiderProperty, value);
    }

    #endregion

    #region Fields

    private PolarChart? _chart;

    private Border? _chartContainer;

    private readonly object _chartLock = new();

    private VisualizationSettingsFlyout? _settingsFlyout;

    private Button? _gearButton;

    #endregion

    #region Construction & layout

    /// <summary>Creates the view and builds its static layout.</summary>
    public SpiderMonitorView()
    {
        CreateLayout();
    }

    private void CreateLayout()
    {
        lock (_chartLock)
        {
            _chartContainer = new Border
            {
                CornerRadius = new CornerRadius(8),
                ClipToBounds = false,
                Background = new SolidColorBrush(ThemeHelper.ChartBackground),
                MinHeight = 200,
                MinWidth = 200,
            };
        }

        _settingsFlyout = new VisualizationSettingsFlyout(SettingsTarget.Spider);
        _gearButton = VisualizationSettingsFlyout.CreateGearButton(_settingsFlyout);
        _gearButton[!Button.ForegroundProperty] = new DynamicResourceExtension(ThemeHelper.TextDimmedBrush);

        var gearPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 4, 2),
            Children = { _gearButton }
        };

        var mainPanel = new DockPanel();
        DockPanel.SetDock(gearPanel, Dock.Top);
        mainPanel.Children.Add(gearPanel);
        mainPanel.Children.Add(_chartContainer);

        Content = mainPanel;
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    protected override void ApplyTheme()
    {
        var bg = new SolidColorBrush(ThemeHelper.ChartBackground);
        if (_chartContainer != null) _chartContainer.Background = bg;
        if (_chart != null) _chart.Background = bg;
    }

    /// <inheritdoc/>
    protected override void CreateSurface()
    {
        lock (_chartLock)
        {
            if (_chart != null || _chartContainer == null || Monitor == null) return;

            _chart = new PolarChart
            {
                MinWidth = 200,
                MinHeight = 200,
                EasingFunction = null,
                AnimationsSpeed = TimeSpan.Zero,
                Background = new SolidColorBrush(ThemeHelper.ChartBackground),
            };

            _chart.Bind(SourceGenChart.SeriesProperty, new Binding("Spider.Series") { Source = this });
            _chart.Bind(SourceGenPolarChart.AngleAxesProperty, new Binding("Spider.AngleAxes") { Source = this });
            _chart.Bind(SourceGenPolarChart.RadiusAxesProperty, new Binding("Spider.RadiusAxes") { Source = this });
            _chart.Bind(SourceGenChart.SyncContextProperty, new Binding("Spider.Sync") { Source = this });

            // Signal the monitor that its chart surface is ready to receive data.
            var monitor = Monitor;
            _chart.AttachedToVisualTree += (_, _) =>
                Dispatcher.UIThread.Post(() => monitor?.NotifyChartReady(), DispatcherPriority.Loaded);

            _chartContainer.Child = _chart;
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
                _chart.ClearValue(SourceGenChart.SeriesProperty);
                _chart.ClearValue(SourceGenPolarChart.AngleAxesProperty);
                _chart.ClearValue(SourceGenPolarChart.RadiusAxesProperty);
                _chart.ClearValue(SourceGenChart.SyncContextProperty);

                _chart.Series = Array.Empty<LiveChartsCore.ISeries>();
                _chart.AngleAxes = Array.Empty<LiveChartsCore.SkiaSharpView.PolarAxis>();
                _chart.RadiusAxes = Array.Empty<LiveChartsCore.SkiaSharpView.PolarAxis>();
                _chart.SyncContext = null!;

                if (_chartContainer != null)
                    _chartContainer.Child = null;
            }
            catch { /* best-effort teardown */ }
            finally
            {
                _chart = null;
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SpiderProperty)
            HandleMonitorChanged(change.NewValue as SpiderMonitor);
    }

    /// <inheritdoc/>
    protected override void OnMonitorAssigned(SpiderMonitor? oldMonitor, SpiderMonitor? newMonitor)
    {
        _settingsFlyout?.SetSpider(newMonitor);
    }

    #endregion
}