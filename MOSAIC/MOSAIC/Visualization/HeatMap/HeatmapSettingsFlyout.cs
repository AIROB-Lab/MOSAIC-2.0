using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
namespace MOSAIC.Visualization.Heatmap;

/// <summary>
/// Scaling modes exposed to the user.
/// </summary>
public enum HeatmapScalingMode
{
    Auto,
    FixedRange,
    PerSensorFixed,
    AdaptiveRms
}

/// <summary>
/// Self-contained flyout content for heatmap settings (scaling mode + colormap).
/// Created by <see cref="VisualizationPanel"/> and attached to a gear button
/// next to the heatmap toggle.
/// </summary>
public sealed class HeatmapSettingsFlyout
{
    private HeatMapMonitor? _heatmap;
    private HeatmapScalingMode _currentMode = HeatmapScalingMode.Auto;

    // UI elements
    private readonly StackPanel _root;
    private readonly ComboBox _modeCombo;
    private readonly StackPanel _fixedRangePanel;
    private readonly TextBox _minInput;
    private readonly TextBox _maxInput;
    private readonly Button _applyRangeBtn;
    private readonly WrapPanel _colormapPanel;
    private readonly TextBlock _statusLabel;

    public Control Content => _root;

    public HeatmapSettingsFlyout()
    {
        var isDark = App.CurrentTheme == AppTheme.Dark;

        // ── Scaling mode ──
        var modeLabel = MakeLabel("Scaling Mode", isDark);

        _modeCombo = new ComboBox
        {
            FontSize = 11,
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[]
            {
                "Auto (global)",
                "Fixed Range",
                "Per-Sensor Fixed",
                "Adaptive RMS"
            },
            SelectedIndex = 0,
        };
        _modeCombo.SelectionChanged += OnModeChanged;

        // ── Fixed range inputs (shown for Fixed Range and Per-Sensor Fixed) ──
        _minInput = MakeTextBox("Min", "-1", isDark);
        _maxInput = MakeTextBox("Max", "1", isDark);

        _applyRangeBtn = new Button
        {
            Content = "Apply",
            FontSize = 10,
            Padding = new Thickness(12, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#3B82F6")),
            Foreground = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 4, 0, 0),
        };
        _applyRangeBtn.Click += OnApplyRange;

        _fixedRangePanel = new StackPanel
        {
            Spacing = 4,
            IsVisible = false,
            Children =
            {
                MakeLabel("Range", isDark),
                new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("*,8,*"),
                    Children =
                    {
                        SetCol(_minInput, 0),
                        SetCol(_maxInput, 2),
                    }
                },
                _applyRangeBtn,
            }
        };

        // ── Colormap picker ──
        var colormapLabel = MakeLabel("Colormap", isDark);

        _colormapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        AddColormapButton("Viridis",  isDark, hm => hm.UseViridis());
        AddColormapButton("Inferno",  isDark, hm => hm.UseInferno());
        AddColormapButton("Plasma",   isDark, hm => hm.UsePlasma());
        AddColormapButton("Turbo",    isDark, hm => hm.UseTurbo());
        AddColormapButton("Hot",      isDark, hm => hm.UseHot());
        AddColormapButton("Grey",     isDark, hm => hm.UseGreyscale());
        AddColormapButton("B-W-R",    isDark, hm => hm.UseBlueWhiteRed());
        AddColormapButton("CoolWarm", isDark, hm => hm.UseCoolWarm());
        AddColormapButton("P-W-G",    isDark, hm => hm.UsePurpleWhiteGreen());

        // ── Status ──
        _statusLabel = new TextBlock
        {
            FontSize = 9,
            Foreground = new SolidColorBrush(isDark ? Color.Parse("#808080") : Color.Parse("#94A3B8")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };

        // ── Root layout ──
        _root = new StackPanel
        {
            Spacing = 8,
            Width = 210,
            Margin = new Thickness(4),
            Children =
            {
                modeLabel,
                _modeCombo,
                _fixedRangePanel,
                new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(isDark ? Color.Parse("#2A2A2A") : Color.Parse("#E2E8F0")),
                    Margin = new Thickness(0, 4),
                },
                colormapLabel,
                _colormapPanel,
                _statusLabel,
            }
        };
    }

    /// <summary>
    /// Binds this flyout to a HeatMap instance. Call when the heatmap property changes.
    /// </summary>
    public void SetHeatmap(HeatMapMonitor? heatmap)
    {
        _heatmap = heatmap;
        UpdateStatus();
    }

    // ── Mode selection ──

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = _modeCombo.SelectedIndex;
        _currentMode = index switch
        {
            0 => HeatmapScalingMode.Auto,
            1 => HeatmapScalingMode.FixedRange,
            2 => HeatmapScalingMode.PerSensorFixed,
            3 => HeatmapScalingMode.AdaptiveRms,
            _ => HeatmapScalingMode.Auto,
        };

        // Show/hide range inputs
        _fixedRangePanel.IsVisible = _currentMode is HeatmapScalingMode.FixedRange or HeatmapScalingMode.PerSensorFixed;

        // Apply immediately for modes that don't need range input
        if (_currentMode == HeatmapScalingMode.Auto)
        {
            _heatmap?.UseGlobalAuto();
            SetStatus("Auto-scaling enabled");
        }
        else if (_currentMode == HeatmapScalingMode.AdaptiveRms)
        {
            _heatmap?.UsePerSensorAdaptiveRms();
            SetStatus("Adaptive RMS enabled");
        }
        else
        {
            SetStatus("Set min/max and press Apply");
        }
    }

    private void OnApplyRange(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_heatmap == null) return;

        if (!double.TryParse(_minInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var min) ||
            !double.TryParse(_maxInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var max))
        {
            SetStatus("Invalid input — use numbers");
            return;
        }

        if (min >= max)
        {
            SetStatus("Min must be less than Max");
            return;
        }

        try
        {
            switch (_currentMode)
            {
                case HeatmapScalingMode.FixedRange:
                    _heatmap.UseGlobalFixed(min, max);
                    SetStatus($"Fixed: [{min:G4}, {max:G4}]");
                    break;

                case HeatmapScalingMode.PerSensorFixed:
                    _heatmap.UsePerSensorFixedForAll(min, max);
                    SetStatus($"Per-sensor: [{min:G4}, {max:G4}]");
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    // ── Colormap buttons ──

    private void AddColormapButton(string label, bool isDark, Action<HeatMapMonitor> apply)
    {
        var btn = new Button
        {
            Content = label,
            FontSize = 9,
            Padding = new Thickness(6, 3),
            Margin = new Thickness(0, 0, 4, 4),
            MinWidth = 0,
            MinHeight = 0,
            Background = new SolidColorBrush(isDark ? Color.Parse("#252525") : Color.Parse("#F1F5F9")),
            Foreground = new SolidColorBrush(isDark ? Color.Parse("#B0B0B0") : Color.Parse("#475569")),
            CornerRadius = new CornerRadius(4),
        };

        btn.Click += (_, _) =>
        {
            if (_heatmap == null) return;
            try
            {
                apply(_heatmap);
                SetStatus($"Colormap: {label}");
            }
            catch (Exception ex)
            {
                SetStatus($"Error: {ex.Message}");
            }
        };

        btn.AddHandler(InputElement.PointerPressedEvent, (_, e) => e.Handled = true,
            Avalonia.Interactivity.RoutingStrategies.Bubble);

        _colormapPanel.Children.Add(btn);
    }

    // ── Helpers ──

    private void SetStatus(string text)
    {
        _statusLabel.Text = text;
    }

    private void UpdateStatus()
    {
        if (_heatmap == null)
            SetStatus("No heatmap");
        else
            SetStatus(_currentMode switch
            {
                HeatmapScalingMode.Auto => "Auto-scaling",
                HeatmapScalingMode.FixedRange => "Fixed range",
                HeatmapScalingMode.PerSensorFixed => "Per-sensor fixed",
                HeatmapScalingMode.AdaptiveRms => "Adaptive RMS",
                _ => ""
            });
    }

    private static TextBlock MakeLabel(string text, bool isDark) => new()
    {
        Text = text,
        FontSize = 9,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(isDark ? Color.Parse("#B0B0B0") : Color.Parse("#475569")),
        Margin = new Thickness(0, 0, 0, 2),
    };

    private static TextBox MakeTextBox(string watermark, string defaultText, bool isDark) => new()
    {
        Text = defaultText,
        Watermark = watermark,
        FontSize = 11,
        Padding = new Thickness(6, 4),
        Background = new SolidColorBrush(isDark ? Color.Parse("#1A1A1A") : Colors.White),
        Foreground = new SolidColorBrush(isDark ? Colors.White : Color.Parse("#0F172A")),
        BorderBrush = new SolidColorBrush(isDark ? Color.Parse("#333333") : Color.Parse("#E2E8F0")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
    };

    private static T SetCol<T>(T control, int col) where T : Control
    {
        Grid.SetColumn(control, col);
        return control;
    }

    /// <summary>
    /// Creates the Flyout to attach to the gear button.
    /// </summary>
    public Flyout CreateFlyout(bool isDark)
    {
        return new Flyout
        {
            Content = _root,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            ShowMode = FlyoutShowMode.TransientWithDismissOnPointerMoveAway,
        };
    }
}