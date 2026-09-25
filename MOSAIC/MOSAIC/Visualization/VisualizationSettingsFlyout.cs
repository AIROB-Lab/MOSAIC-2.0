using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MOSAIC.Visualization.Heatmap;
using MOSAIC.Visualization.ScopeMonitor;
using MOSAIC.Visualization.SnapshotMonitor;

namespace MOSAIC.Visualization;

/// <summary>
/// Which visualization type this flyout controls.
/// </summary>
public enum SettingsTarget
{
    Scope,
    Spider,
    Heatmap,
    Snapshot
}

/// <summary>
/// Unified settings flyout for all visualization types.
/// 
/// Common:  Autoscale toggle + optional fixed min/max inputs.
/// Scope:   Additionally shows channel offset control (auto/manual).
/// Heatmap: Additionally shows scaler mode selector + colormap picker.
/// 
/// Created by each *View class and attached to a gear button.
/// </summary>
public sealed class VisualizationSettingsFlyout
{
    #region Fields

    private readonly SettingsTarget _target;

    // Monitor references (only one is set at a time)
    private ScopeMonitor.ScopeMonitor? _scope;
    private SpiderMonitor.SpiderMonitor? _spider;
    private HeatMapMonitor? _heatmap;
    private SnapshotMonitor.SnapshotMonitor? _snapshot;

    // UI
    private readonly StackPanel _root;
    private readonly ToggleButton _autoToggle;
    private readonly StackPanel _fixedPanel;
    private readonly TextBox _minInput;
    private readonly TextBox _maxInput;
    private readonly Button _applyBtn;
    private readonly TextBlock _statusLabel;

    // Scope offset controls
    private readonly ToggleButton? _offsetAutoToggle;
    private readonly StackPanel? _offsetManualPanel;
    private readonly TextBox? _offsetInput;
    private readonly Button? _offsetApplyBtn;

    // Heatmap-only
    private readonly StackPanel? _heatmapSection;
    private readonly ComboBox? _scalerCombo;
    private readonly WrapPanel? _colormapPanel;
    private readonly TextBox? _rowsInput;
    private readonly TextBox? _colsInput;
    private readonly Button? _applyGridBtn;

    #endregion

    #region Construction

    public Control Content => _root;

    public VisualizationSettingsFlyout(SettingsTarget target)
    {
        _target = target;
        var isDark = App.CurrentTheme == AppTheme.Dark;

        // Autoscale toggle
        _autoToggle = new ToggleButton
        {
            Content = "Autoscale",
            IsChecked = true,
            FontSize = 11,
            Padding = new Thickness(10, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(4),
        };
        _autoToggle.Click += OnAutoToggleClick;

        // Fixed range inputs
        _minInput = MakeTextBox("Min", "-1", isDark);
        _maxInput = MakeTextBox("Max", "1", isDark);

        _applyBtn = new Button
        {
            Content = "Apply",
            FontSize = 10,
            Padding = new Thickness(10, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#3B82F6")),
            Foreground = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 4, 0, 0),
        };
        _applyBtn.Click += OnApplyRange;

        _fixedPanel = new StackPanel
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
                _applyBtn,
            }
        };

        // Status
        _statusLabel = new TextBlock
        {
            FontSize = 9,
            Foreground = new SolidColorBrush(isDark ? Color.Parse("#808080") : Color.Parse("#94A3B8")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Text = "Autoscale",
        };

        // Root layout
        _root = new StackPanel
        {
            Spacing = 6,
            Width = 200,
            Margin = new Thickness(4),
            Children =
            {
                MakeLabel(GetTitle(), isDark),
                _autoToggle,
                _fixedPanel,
            }
        };

        // Scope-only: channel offset control
        if (target == SettingsTarget.Scope)
        {
            _root.Children.Add(MakeSeparator(isDark));
            _root.Children.Add(MakeLabel("Channel Offset (Stacked)", isDark));

            _offsetAutoToggle = new ToggleButton
            {
                Content = "Auto Offset",
                IsChecked = true,
                FontSize = 11,
                Padding = new Thickness(10, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(4),
            };
            _offsetAutoToggle.Click += OnOffsetAutoToggleClick;
            _root.Children.Add(_offsetAutoToggle);

            _offsetInput = MakeTextBox("Offset", "1.0", isDark);

            _offsetApplyBtn = new Button
            {
                Content = "Apply Offset",
                FontSize = 10,
                Padding = new Thickness(10, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse("#3B82F6")),
                Foreground = new SolidColorBrush(Colors.White),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 4, 0, 0),
            };
            _offsetApplyBtn.Click += OnApplyOffset;

            _offsetManualPanel = new StackPanel
            {
                Spacing = 4,
                IsVisible = false,
                Children = { _offsetInput, _offsetApplyBtn }
            };
            _root.Children.Add(_offsetManualPanel);
        }

        // Heatmap-only: grid layout + scaler mode + colormap picker
        if (target == SettingsTarget.Heatmap)
        {
            _root.Children.Add(MakeSeparator(isDark));

            // Grid layout: rows × columns
            _rowsInput = MakeTextBox("Rows", "1", isDark);
            _colsInput = MakeTextBox("Cols", "16", isDark);

            _applyGridBtn = new Button
            {
                Content = "Apply Layout",
                FontSize = 10,
                Padding = new Thickness(10, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse("#3B82F6")),
                Foreground = new SolidColorBrush(Colors.White),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 4, 0, 0),
            };
            _applyGridBtn.Click += OnApplyGrid;

            _root.Children.Add(MakeLabel("Grid Layout", isDark));
            _root.Children.Add(new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,*"),
                Children =
                {
                    SetCol(_rowsInput, 0),
                    SetCol(new TextBlock
                    {
                        Text = "×",
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = new SolidColorBrush(isDark ? Color.Parse("#808080") : Color.Parse("#94A3B8")),
                        Margin = new Thickness(6, 0),
                    }, 1),
                    SetCol(_colsInput, 2),
                }
            });
            _root.Children.Add(_applyGridBtn);

            _root.Children.Add(MakeSeparator(isDark));

            _scalerCombo = new ComboBox
            {
                FontSize = 11,
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
            _scalerCombo.SelectionChanged += OnScalerChanged;

            _root.Children.Add(MakeLabel("Scaler Mode", isDark));
            _root.Children.Add(_scalerCombo);

            _root.Children.Add(MakeSeparator(isDark));
            _root.Children.Add(MakeLabel("Colormap", isDark));

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
            AddColormapButton("Rainbow",  isDark, hm => hm.UseRainbow());

            _root.Children.Add(_colormapPanel);
        }

        _root.Children.Add(_statusLabel);
    }

    #endregion

    #region Public API

    public void SetScope(ScopeMonitor.ScopeMonitor? scope)
    {
        _scope = scope;
        _spider = null;
        _heatmap = null;
        _snapshot = null;
        SyncToggle();
        SyncOffsetControls();
    }

    public void SetSpider(SpiderMonitor.SpiderMonitor? spider)
    {
        _scope = null;
        _spider = spider;
        _heatmap = null;
        _snapshot = null;
        SyncToggle();
    }

    public void SetHeatmap(HeatMapMonitor? heatmap)
    {
        _scope = null;
        _spider = null;
        _heatmap = heatmap;
        _snapshot = null;
        SyncToggle();
        SyncGridInputs();
    }

    public void SetSnapshot(SnapshotMonitor.SnapshotMonitor? snapshot)
    {
        _scope = null;
        _spider = null;
        _heatmap = null;
        _snapshot = snapshot;
        SyncToggle();
    }

    public Flyout CreateFlyout()
    {
        return new Flyout
        {
            Content = _root,
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };
    }

    private void SyncGridInputs()
    {
        if (_heatmap == null || _rowsInput == null || _colsInput == null) return;
        _rowsInput.Text = _heatmap.Rows.ToString();
        _colsInput.Text = _heatmap.Columns.ToString();
    }

    #endregion

    #region Autoscale toggle

    private void SyncToggle()
    {
        bool isAuto = _target switch
        {
            SettingsTarget.Scope => _scope?.IsAutoScale ?? true,
            SettingsTarget.Spider => _spider?.IsAutoScale ?? true,
            SettingsTarget.Snapshot => _snapshot?.AutoScale ?? true,
            SettingsTarget.Heatmap => true, // heatmap scaler handles this differently
            _ => true,
        };
        _autoToggle.IsChecked = isAuto;
        _fixedPanel.IsVisible = !isAuto;
        SetStatus(isAuto ? "Autoscale" : "Fixed range");
    }

    private void OnAutoToggleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        bool isAuto = _autoToggle.IsChecked == true;
        _fixedPanel.IsVisible = !isAuto;

        if (isAuto)
        {
            _scope?.SetAutoScale();
            _spider?.SetAutoScale();
            _snapshot?.SetAutoScale();
            if (_heatmap != null)
            {
                _heatmap.UseGlobalAuto();
                if (_scalerCombo != null) _scalerCombo.SelectedIndex = 0;
            }
            SetStatus("Autoscale");
        }
        else
        {
            SetStatus("Set min/max and press Apply");
        }
    }

    private void OnApplyRange(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!TryParseRange(out var min, out var max)) return;

        try
        {
            switch (_target)
            {
                case SettingsTarget.Scope:
                    _scope?.SetFixedRange(min, max);
                    break;
                case SettingsTarget.Spider:
                    _spider?.SetFixedRange(min, max);
                    break;
                case SettingsTarget.Snapshot:
                    _snapshot?.SetFixedRange(min, max);
                    break;
                case SettingsTarget.Heatmap:
                    ApplyHeatmapRange(min, max);
                    break;
            }
            SetStatus($"Fixed: [{min:G4}, {max:G4}]");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    #endregion

    #region Scope channel offset

    /// <summary>
    /// Syncs the offset controls to the current scope state.
    /// Called when a new scope is attached via <see cref="SetScope"/>.
    /// </summary>
    private void SyncOffsetControls()
    {
        if (_scope == null || _offsetAutoToggle == null || _offsetManualPanel == null || _offsetInput == null)
            return;

        bool isAuto = _scope.AutoOffset;
        _offsetAutoToggle.IsChecked = isAuto;
        _offsetManualPanel.IsVisible = !isAuto;
        _offsetInput.Text = _scope.ChannelOffset.ToString("G4", CultureInfo.InvariantCulture);
    }

    private void OnOffsetAutoToggleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_scope == null || _offsetAutoToggle == null || _offsetManualPanel == null) return;

        bool isAuto = _offsetAutoToggle.IsChecked == true;
        _offsetManualPanel.IsVisible = !isAuto;
        _scope.AutoOffset = isAuto;

        if (isAuto)
        {
            // Re-enable auto: unlock the settle computation so it recalculates
            _scope.ResetAutoOffsetPublic();
            SetStatus("Auto offset");
        }
        else
        {
            // Switch to manual: populate the input with the current computed value
            if (_offsetInput != null)
                _offsetInput.Text = _scope.ChannelOffset.ToString("G4", CultureInfo.InvariantCulture);
            SetStatus($"Manual offset: {_scope.ChannelOffset:G4}");
        }
    }

    private void OnApplyOffset(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_scope == null || _offsetInput == null) return;

        if (!double.TryParse(_offsetInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var offset))
        {
            SetStatus("Invalid offset — use a number");
            return;
        }

        if (offset <= 0)
        {
            SetStatus("Offset must be > 0");
            return;
        }

        _scope.AutoOffset = false;
        if (_offsetAutoToggle != null) _offsetAutoToggle.IsChecked = false;
        _scope.ChannelOffset = offset;
        SetStatus($"Manual offset: {offset:G4}");
    }

    #endregion

    #region Heatmap controls

    private void OnApplyGrid(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_heatmap == null) return;

        if (!int.TryParse(_rowsInput?.Text, out var rows) || rows <= 0 ||
            !int.TryParse(_colsInput?.Text, out var cols) || cols <= 0)
        {
            SetStatus("Invalid grid — use positive integers");
            return;
        }

        try
        {
            _heatmap.ConfigureGrid(rows, cols);
            SetStatus($"Grid: {rows} × {cols} ({rows * cols} cells)");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private void OnScalerChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_heatmap == null || _scalerCombo == null) return;

        var index = _scalerCombo.SelectedIndex;
        try
        {
            switch (index)
            {
                case 0: // Auto
                    _heatmap.UseGlobalAuto();
                    _autoToggle.IsChecked = true;
                    _fixedPanel.IsVisible = false;
                    SetStatus("Auto-scaling");
                    break;
                case 1: // Fixed Range
                    _autoToggle.IsChecked = false;
                    _fixedPanel.IsVisible = true;
                    SetStatus("Set min/max and press Apply");
                    break;
                case 2: // Per-Sensor Fixed
                    _autoToggle.IsChecked = false;
                    _fixedPanel.IsVisible = true;
                    SetStatus("Set per-sensor range");
                    break;
                case 3: // Adaptive RMS
                    _heatmap.UsePerSensorAdaptiveRms();
                    _autoToggle.IsChecked = true;
                    _fixedPanel.IsVisible = false;
                    SetStatus("Adaptive RMS");
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private void ApplyHeatmapRange(double min, double max)
    {
        if (_heatmap == null || _scalerCombo == null) return;

        switch (_scalerCombo.SelectedIndex)
        {
            case 1: // Fixed Range (global)
                _heatmap.UseGlobalFixed(min, max);
                break;
            case 2: // Per-Sensor Fixed
                _heatmap.UsePerSensorFixedForAll(min, max);
                break;
            default:
                _heatmap.UseGlobalFixed(min, max);
                break;
        }
    }

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

        _colormapPanel?.Children.Add(btn);
    }

    #endregion

    #region Helpers

    private bool TryParseRange(out double min, out double max)
    {
        min = max = 0;
        if (!double.TryParse(_minInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out min) ||
            !double.TryParse(_maxInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out max))
        {
            SetStatus("Invalid input — use numbers");
            return false;
        }
        if (min >= max)
        {
            SetStatus("Min must be less than Max");
            return false;
        }
        return true;
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    private string GetTitle() => _target switch
    {
        SettingsTarget.Scope => "Scope Settings",
        SettingsTarget.Spider => "Spider Settings",
        SettingsTarget.Heatmap => "Heatmap Settings",
        SettingsTarget.Snapshot => "Snapshot Settings",
        _ => "Settings",
    };

    private static TextBlock MakeLabel(string text, bool isDark) => new()
    {
        Text = text,
        FontSize = 9,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(isDark ? Color.Parse("#B0B0B0") : Color.Parse("#475569")),
        Margin = new Thickness(0, 0, 0, 2),
    };

    private static TextBox MakeTextBox(string watermark, string text, bool isDark) => new()
    {
        Text = text,
        PlaceholderText = watermark,
        FontSize = 11,
        Padding = new Thickness(6, 4),
        Background = new SolidColorBrush(isDark ? Color.Parse("#1A1A1A") : Colors.White),
        Foreground = new SolidColorBrush(isDark ? Colors.White : Color.Parse("#0F172A")),
        BorderBrush = new SolidColorBrush(isDark ? Color.Parse("#333333") : Color.Parse("#E2E8F0")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
    };

    private static Border MakeSeparator(bool isDark) => new()
    {
        Height = 1,
        Background = new SolidColorBrush(isDark ? Color.Parse("#2A2A2A") : Color.Parse("#E2E8F0")),
        Margin = new Thickness(0, 2),
    };

    private static T SetCol<T>(T control, int col) where T : Control
    {
        Grid.SetColumn(control, col);
        return control;
    }

    /// <summary>
    /// Helper to create a standard gear button with this flyout attached.
    /// </summary>
    public static Button CreateGearButton(VisualizationSettingsFlyout flyout)
    {
        var isDark = App.CurrentTheme == AppTheme.Dark;
        var btn = new Button
        {
            Content = "\u2699",
            FontSize = 14,
            Padding = new Thickness(2),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(isDark ? Colors.SlateGray : Color.Parse("#94A3B8")),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Flyout = flyout.CreateFlyout(),
        };
        btn.AddHandler(InputElement.PointerPressedEvent, (_, e) => e.Handled = true,
            Avalonia.Interactivity.RoutingStrategies.Bubble);
        return btn;
    }

    #endregion
}
