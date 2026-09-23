using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using MOSAIC.Visualization;
using MOSAIC.Visualization.ScopeMonitor;
using ScottPlot.Avalonia;

namespace MOSAIC.Visualization.SnapshotMonitor;

/// <summary>
/// Hosts a ScottPlot <see cref="AvaPlot"/> bound to a <see cref="SnapshotMonitor"/>.
///
/// <para><b>Legend:</b> Colored dots with channel labels. Click to solo; click again to restore all.
/// An "All" label provides an explicit restore-all target.</para>
/// <para><b>Settings:</b> Gear button opens autoscale/fixed-range flyout.</para>
/// <para><b>Sizing:</b> S / M / L buttons matching <see cref="VisualizationPanel"/> pattern.</para>
/// </summary>
public class SnapshotMonitorView : UserControl
{
    #region Constants

    private static readonly HeightRange Heights = new(100, 600, 220);

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<SnapshotMonitor?> SnapshotProperty =
        AvaloniaProperty.Register<SnapshotMonitorView, SnapshotMonitor?>(nameof(Snapshot));

    public SnapshotMonitor? Snapshot
    {
        get => GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    #endregion

    #region Fields

    private AvaPlot? _avaPlot;
    private Border? _chartContainer;
    private WrapPanel? _legendPanel;
    private VisualizationSettingsFlyout? _settingsFlyout;
    private Button? _gearButton;
    private Canvas? _sizeTrack;
    private Border? _sizeThumb;
    private SnapshotMonitor? _currentSnapshot;
    private bool _themeSubscribed;
    private bool _isAttached;
    private bool _dragging;

    #endregion

    #region Legend State

    private int _soloIndex = -1;
    private readonly List<(Border Outer, TextBlock Label)> _legendItems = new();

    #endregion

    public SnapshotMonitorView()
    {
        CreateLayout();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        SubscribeTheme();
    }

    #region Theme

    private void SubscribeTheme()
    {
        if (_themeSubscribed) return;
        App.ThemeChanged += OnThemeChanged;
        _themeSubscribed = true;
    }

    private void UnsubscribeTheme()
    {
        if (!_themeSubscribed) return;
        App.ThemeChanged -= OnThemeChanged;
        _themeSubscribed = false;
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateContainerBackground();
            _currentSnapshot?.UpdateThemeColors();
        }, DispatcherPriority.Render);
    }

    private void UpdateContainerBackground()
    {
        var bg = new SolidColorBrush(ThemeHelper.ChartBackground);
        if (_chartContainer != null) _chartContainer.Background = bg;
    }

    #endregion

    #region Layout

    private void CreateLayout()
    {
        // ── Legend dots (left) ──
        _legendPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0)
        };

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

        // Position thumb at default
        double tDefault = (Heights.Default - Heights.Min) / (Heights.Max - Heights.Min);
        Canvas.SetLeft(_sizeThumb, tDefault * (trackWidth - thumbSize));

        _sizeTrack = new Canvas
        {
            Width = trackWidth,
            Height = canvasHeight,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Children = { rail, _sizeThumb },
        };

        _sizeTrack.PointerPressed += (_, e) =>
        {
            _dragging = true;
            e.Pointer.Capture(_sizeTrack);
            ApplyTrackPosition(e.GetPosition(_sizeTrack).X, trackWidth, thumbSize);
            e.Handled = true;
        };

        _sizeTrack.PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            ApplyTrackPosition(e.GetPosition(_sizeTrack).X, trackWidth, thumbSize);
        };

        _sizeTrack.PointerReleased += (_, e) =>
        {
            _dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };

        // ── Settings gear ──
        _settingsFlyout = new VisualizationSettingsFlyout(SettingsTarget.Snapshot);
        _gearButton = VisualizationSettingsFlyout.CreateGearButton(_settingsFlyout);
        _gearButton[!Button.ForegroundProperty] =
            new DynamicResourceExtension(ThemeHelper.TextDimmedBrush);

        var rightControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _sizeTrack, _gearButton }
        };

        var toolbar = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 2)
        };
        DockPanel.SetDock(rightControls, Dock.Right);
        toolbar.Children.Add(rightControls);
        toolbar.Children.Add(_legendPanel);

        // ── Chart container ──
        _chartContainer = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = new SolidColorBrush(ThemeHelper.ChartBackground),
            MinHeight = Heights.Min,
            MinWidth = 200,
            Height = Heights.Default
        };

        // ── Assemble ──
        var mainPanel = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        mainPanel.Children.Add(toolbar);
        mainPanel.Children.Add(_chartContainer);
        Content = mainPanel;
    }

    private void ApplyTrackPosition(double x, double trackWidth, double thumbSize)
    {
        double t = System.Math.Clamp(x / trackWidth, 0, 1);
        double newHeight = Heights.Min + t * (Heights.Max - Heights.Min);

        if (_chartContainer != null)
            _chartContainer.Height = newHeight;

        if (_sizeThumb != null)
            Canvas.SetLeft(_sizeThumb, t * (trackWidth - thumbSize));
    }

    #endregion

    #region Legend Dots

    private void RebuildLegend()
    {
        if (_legendPanel == null || _currentSnapshot == null) return;

        _legendPanel.Children.Clear();
        _legendItems.Clear();
        _soloIndex = -1;

        int count = _currentSnapshot.TraceCount;
        if (count <= 0) return;

        // "All" label — always-visible unsolo target
        var allLabel = new TextBlock
        {
            Text = "All",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 6, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        allLabel[!TextBlock.ForegroundProperty] =
            new DynamicResourceExtension(ThemeHelper.TextPrimaryBrush);
        allLabel.Tapped += (_, _) =>
        {
            _soloIndex = -1;
            _currentSnapshot?.ShowAllTraces();
            UpdateDotHighlights();
        };
        _legendPanel.Children.Add(allLabel);

        // Per-config legend items: [dot] [label]
        for (int t = 0; t < count; t++)
        {
            var skColor = ScopeMonitor.ScopeMonitor.GetChannelColor(t);
            var avColor = Color.FromRgb(skColor.Red, skColor.Green, skColor.Blue);
            var colorBrush = new SolidColorBrush(avColor);
            int traceIndex = t;

            // Colored dot
            var innerDot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = colorBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Outer ring for highlight
            var outerDot = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Margin = new Thickness(0, 0, 2, 0),
                Child = innerDot
            };

            // Label
            var label = new TextBlock
            {
                Text = $"Ch {t}",
                FontSize = 10,
                Foreground = colorBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            // Clickable container for dot + label
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Cursor = new Cursor(StandardCursorType.Hand),
                Children = { outerDot, label },
                [ToolTip.TipProperty] = $"Config {t} — click to solo"
            };
            item.Tapped += (_, _) => OnDotClicked(traceIndex);

            _legendItems.Add((outerDot, label));
            _legendPanel.Children.Add(item);
        }
    }

    private void OnDotClicked(int index)
    {
        if (_currentSnapshot == null) return;

        if (_soloIndex == index)
        {
            // Already solo'd on this one → restore all
            _soloIndex = -1;
            _currentSnapshot.ShowAllTraces();
        }
        else
        {
            // Solo this trace
            _soloIndex = index;
            _currentSnapshot.ShowSingleTrace(index);
        }

        UpdateDotHighlights();
    }

    private void UpdateDotHighlights()
    {
        for (int i = 0; i < _legendItems.Count; i++)
        {
            var (outerDot, label) = _legendItems[i];
            var skColor = ScopeMonitor.ScopeMonitor.GetChannelColor(i);
            var avColor = Color.FromRgb(skColor.Red, skColor.Green, skColor.Blue);

            if (_soloIndex < 0)
            {
                // All visible
                outerDot.Opacity = 1.0;
                outerDot.BorderBrush = new SolidColorBrush(Colors.Transparent);
                label.Opacity = 1.0;
            }
            else if (i == _soloIndex)
            {
                // Solo'd — highlight ring
                outerDot.Opacity = 1.0;
                outerDot.BorderBrush = new SolidColorBrush(avColor);
                label.Opacity = 1.0;
                label.FontWeight = FontWeight.Bold;
            }
            else
            {
                // Dimmed
                outerDot.Opacity = 0.25;
                outerDot.BorderBrush = new SolidColorBrush(Colors.Transparent);
                label.Opacity = 0.25;
                label.FontWeight = FontWeight.Normal;
            }

            // Reset bold when not solo'd
            if (_soloIndex < 0)
                label.FontWeight = FontWeight.Normal;
        }
    }

    #endregion

    #region Property Changes

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SnapshotProperty)
            OnSnapshotChanged(change.OldValue as SnapshotMonitor, change.NewValue as SnapshotMonitor);
    }

    private void OnSnapshotChanged(SnapshotMonitor? oldSnapshot, SnapshotMonitor? newSnapshot)
    {
        if (oldSnapshot != null)
        {
            oldSnapshot.Pause();
            oldSnapshot.DetachPlot();
            oldSnapshot.TracesChanged -= OnTracesChanged;
        }

        _currentSnapshot = newSnapshot;
        _settingsFlyout?.SetSnapshot(newSnapshot);

        if (newSnapshot != null)
        {
            newSnapshot.TracesChanged += OnTracesChanged;

            if (_isAttached)
                BuildAndAttachPlot();
        }
    }

    private void OnTracesChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnTracesChanged, DispatcherPriority.Render);
            return;
        }

        RebuildLegend();
    }

    #endregion

    #region Attach / Detach

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = true;
        SubscribeTheme();

        if (_currentSnapshot != null)
        {
            _currentSnapshot.TracesChanged -= OnTracesChanged;
            _currentSnapshot.TracesChanged += OnTracesChanged;
            BuildAndAttachPlot();
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        UnsubscribeTheme();
        if (_currentSnapshot != null)
        {
            _currentSnapshot.Pause();
            _currentSnapshot.DetachPlot();
            _currentSnapshot.TracesChanged -= OnTracesChanged;
        }
        DestroyAvaPlot();
    }

    private void BuildAndAttachPlot()
    {
        if (_currentSnapshot == null) return;

        _currentSnapshot.Pause();
        DestroyAvaPlot();

        _avaPlot = new AvaPlot
        {
            MinWidth = 200,
            MinHeight = 120,
        };

        if (_chartContainer != null)
            _chartContainer.Child = _avaPlot;

        _currentSnapshot.ResetData();
        _currentSnapshot.AttachPlot(_avaPlot.Plot, () =>
        {
            _avaPlot?.Refresh();
        });

        _settingsFlyout?.SetSnapshot(_currentSnapshot);
        UpdateContainerBackground();
        _currentSnapshot.UpdateThemeColors();
        RebuildLegend();
        _currentSnapshot.Resume();
    }

    private void DestroyAvaPlot()
    {
        if (_avaPlot == null) return;

        if (_chartContainer != null)
            _chartContainer.Child = null;

        _avaPlot = null;
    }

    #endregion
}