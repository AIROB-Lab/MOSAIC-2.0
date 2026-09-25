using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MOSAIC.Visualization.SpectrogramMonitor;

/// <summary>
/// Self-contained flyout content for spectrogram settings: scaling mode (auto / fixed range)
/// and colormap selection. Attach to a <see cref="SpectrogramMonitor"/> via <see cref="SetMonitor"/>.
/// </summary>
public sealed class SpectrogramSettingsFlyout
{
    #region Fields

    private SpectrogramMonitor? _monitor;

    // UI elements
    private readonly StackPanel _root;

    private readonly ComboBox _modeCombo;

    private readonly StackPanel _fixedRangePanel;

    private readonly TextBox _minInput;

    private readonly TextBox _maxInput;

    private readonly Button _applyRangeBtn;

    private readonly WrapPanel _colormapPanel;

    private readonly TextBlock _statusLabel;

    #endregion

    #region Construction

    public SpectrogramSettingsFlyout()
    {
        var isDark = App.CurrentTheme == AppTheme.Dark;

        // Scaling mode
        var modeLabel = MakeLabel("Scaling Mode", isDark);

        _modeCombo = new ComboBox
        {
            FontSize = 11,
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Auto", "Fixed Range" },
            SelectedIndex = 0,
        };
        _modeCombo.SelectionChanged += OnModeChanged;

        // Fixed range inputs
        _minInput = MakeTextBox("Min", "-120", isDark);
        _maxInput = MakeTextBox("Max", "0", isDark);

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

        // Colormap picker
        var colormapLabel = MakeLabel("Colormap", isDark);

        _colormapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        AddColormapButton("Viridis",  isDark, m => m.UseViridis());
        AddColormapButton("Inferno",  isDark, m => m.UseInferno());
        AddColormapButton("Plasma",   isDark, m => m.UsePlasma());
        AddColormapButton("Turbo",    isDark, m => m.UseTurbo());
        AddColormapButton("Hot",      isDark, m => m.UseHot());
        AddColormapButton("Grey",     isDark, m => m.UseGreyscale());
        AddColormapButton("B-W-R",    isDark, m => m.UseBlueWhiteRed());
        AddColormapButton("CoolWarm", isDark, m => m.UseCoolWarm());
        AddColormapButton("P-W-G",    isDark, m => m.UsePurpleWhiteGreen());

        // Status
        _statusLabel = new TextBlock
        {
            FontSize = 9,
            Foreground = new SolidColorBrush(isDark ? Color.Parse("#808080") : Color.Parse("#94A3B8")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };

        // Root layout
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

    #endregion

    #region Public API

    public Control Content => _root;

    public void SetMonitor(SpectrogramMonitor? monitor)
    {
        _monitor = monitor;
        UpdateStatus();
    }

    public Flyout CreateFlyout(bool isDark)
    {
        return new Flyout
        {
            Content = _root,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            ShowMode = FlyoutShowMode.TransientWithDismissOnPointerMoveAway,
        };
    }

    #endregion

    #region Event handlers

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = _modeCombo.SelectedIndex;
        _fixedRangePanel.IsVisible = index == 1;

        if (index == 0 && _monitor != null)
        {
            _monitor.UseAutoScale();
            SetStatus("Auto-scaling enabled");
        }
        else
        {
            SetStatus("Set min/max and press Apply");
        }
    }

    private void OnApplyRange(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_monitor == null) return;

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
            _monitor.UseFixedScale(min, max);
            SetStatus($"Fixed: [{min:G4}, {max:G4}]");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    #endregion

    #region Helpers

    private void AddColormapButton(string label, bool isDark, Action<SpectrogramMonitor> apply)
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
            if (_monitor == null) return;
            try
            {
                apply(_monitor);
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

    private void SetStatus(string text) => _statusLabel.Text = text;

    private void UpdateStatus()
    {
        if (_monitor == null)
            SetStatus("No spectrogram");
        else
            SetStatus(_monitor.AutoScale ? "Auto-scaling" : "Fixed range");
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
        PlaceholderText = watermark,
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

    #endregion
}
