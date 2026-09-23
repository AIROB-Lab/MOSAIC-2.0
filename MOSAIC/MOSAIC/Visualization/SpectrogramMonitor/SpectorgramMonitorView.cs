using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MOSAIC.Visualization.SpectrogramMonitor;

/// <summary>
/// Code-only Avalonia view for <see cref="SpectrogramMonitor"/>. Renders the spectrogram
/// bitmap with a frequency axis and a colorbar legend. The shared attach/detach, theme, and
/// rebuild lifecycle lives in <see cref="MonitorViewBase{TMonitor}"/>; this class supplies the
/// image/colorbar bindings and the frequency-axis labels.
/// </summary>
public class SpectrogramMonitorView : MonitorViewBase<SpectrogramMonitor>
{
    #region Styled property

    public static readonly StyledProperty<object?> SpectrogramProperty =
        AvaloniaProperty.Register<SpectrogramMonitorView, object?>(nameof(Spectrogram));

    public object? Spectrogram
    {
        get => GetValue(SpectrogramProperty);
        set => SetValue(SpectrogramProperty, value);
    }

    #endregion

    #region Fields

    private Canvas? _freqAxisCanvas;

    private readonly List<TextBlock> _freqTickLabels = new();

    private const int FreqTickCount = 6; // number of tick labels on y-axis

    private const double FreqAxisWidth = 55;

    // Layout elements
    private Image? _spectrogramImage;

    private Border? _imageContainer;

    private Grid? _legendGrid;

    private Image? _colorbarImage;

    private TextBlock? _maxLabel;

    private TextBlock? _minLabel;

    private TextBlock? _captionLabel;

    private TextBlock? _xLabel;

    private Button? _gearButton;

    private SpectrogramSettingsFlyout? _settingsFlyout;

    private readonly object _bindLock = new();

    #endregion

    #region Construction & layout

    /// <summary>Creates the view and builds its static layout.</summary>
    public SpectrogramMonitorView()
    {
        CreateLayout();
    }

    private void CreateLayout()
    {
        const double legendWidth = 64;

        _settingsFlyout = new SpectrogramSettingsFlyout();
        var isDark = App.CurrentTheme == AppTheme.Dark;
        var flyout = _settingsFlyout.CreateFlyout(isDark);

        _gearButton = new Button
        {
            Content = "⚙",
            FontSize = 14,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Center,
            Flyout = flyout,
        };
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

        // Legend grid: gear | max | colorbar (stretch) | min | caption
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

        _spectrogramImage = new Image
        {
            Stretch = Stretch.Fill,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        RenderOptions.SetBitmapInterpolationMode(_spectrogramImage, BitmapInterpolationMode.LowQuality);

        _freqAxisCanvas = new Canvas
        {
            Width = FreqAxisWidth,
            ClipToBounds = true,
        };

        for (int i = 0; i < FreqTickCount; i++)
        {
            var tick = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeight.Medium,
                TextAlignment = TextAlignment.Right,
                Width = FreqAxisWidth - 6,
                Margin = new Thickness(0, 0, 6, 0),
            };
            tick[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(ThemeHelper.TextPrimaryBrush);
            _freqTickLabels.Add(tick);
            _freqAxisCanvas.Children.Add(tick);
        }

        var imageWithAxis = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse($"{FreqAxisWidth},*"),
        };
        Grid.SetColumn(_freqAxisCanvas, 0);  imageWithAxis.Children.Add(_freqAxisCanvas);
        Grid.SetColumn(_spectrogramImage, 1); imageWithAxis.Children.Add(_spectrogramImage);

        _imageContainer = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = new SolidColorBrush(ThemeHelper.ChartBackground),
            Height = 180,
            Child = imageWithAxis,
        };

        _xLabel = new TextBlock
        {
            Text = "← Older          Time          Newer →",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _xLabel[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(ThemeHelper.TextDimmedBrush);

        var contentGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ClipToBounds = true,
        };
        Grid.SetColumn(_imageContainer, 0); contentGrid.Children.Add(_imageContainer);
        Grid.SetColumn(_legendGrid, 1);     contentGrid.Children.Add(_legendGrid);

        var mainStack = new DockPanel();
        DockPanel.SetDock(_xLabel, Dock.Bottom);
        mainStack.Children.Add(_xLabel);
        mainStack.Children.Add(contentGrid);

        var root = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(ThemeHelper.ChartBackground),
            Child = mainStack,
        };

        Content = root;
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    protected override void ApplyTheme()
    {
        var bg = new SolidColorBrush(ThemeHelper.ChartBackground);
        if (_imageContainer != null) _imageContainer.Background = bg;
        if (_legendGrid != null) _legendGrid.Background = bg;
        if (Content is Border root) root.Background = bg;
    }

    /// <inheritdoc/>
    protected override void CreateSurface()
    {
        lock (_bindLock)
        {
            if (Monitor == null) return;

            _spectrogramImage?.Bind(Image.SourceProperty,
                new Binding(nameof(SpectrogramMonitor.Image)) { Source = Monitor, Mode = BindingMode.OneWay });

            _colorbarImage?.Bind(Image.SourceProperty,
                new Binding(nameof(SpectrogramMonitor.ColorbarBitmap)) { Source = Monitor });
            _maxLabel?.Bind(TextBlock.TextProperty,
                new Binding(nameof(SpectrogramMonitor.ColorbarMaxLabel)) { Source = Monitor });
            _minLabel?.Bind(TextBlock.TextProperty,
                new Binding(nameof(SpectrogramMonitor.ColorbarMinLabel)) { Source = Monitor });
            _captionLabel?.Bind(TextBlock.TextProperty,
                new Binding(nameof(SpectrogramMonitor.ColorbarCaption)) { Source = Monitor });

            // Subscribe to frequency range changes for y-axis labels
            Monitor.PropertyChanged += OnMonitorPropertyChanged;
            UpdateFrequencyAxis();
        }
    }

    /// <inheritdoc/>
    protected override void DestroySurface()
    {
        lock (_bindLock)
        {
            try
            {
                if (Monitor != null)
                    Monitor.PropertyChanged -= OnMonitorPropertyChanged;

                _spectrogramImage?.ClearValue(Image.SourceProperty);
                _spectrogramImage!.Source = null;

                _colorbarImage?.ClearValue(Image.SourceProperty);
                _maxLabel?.ClearValue(TextBlock.TextProperty);
                _minLabel?.ClearValue(TextBlock.TextProperty);
                _captionLabel?.ClearValue(TextBlock.TextProperty);
            }
            catch { /* swallow */ }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SpectrogramProperty)
            HandleMonitorChanged(change.NewValue as SpectrogramMonitor);
    }

    /// <inheritdoc/>
    protected override void OnMonitorAssigned(SpectrogramMonitor? oldMonitor, SpectrogramMonitor? newMonitor)
    {
        _settingsFlyout?.SetMonitor(newMonitor);
        newMonitor?.RebuildColorbar();
    }

    #endregion

    #region Frequency axis

    private void OnMonitorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SpectrogramMonitor.MinFrequency) or nameof(SpectrogramMonitor.MaxFrequency))
        {
            Dispatcher.UIThread.Post(UpdateFrequencyAxis, DispatcherPriority.Background);
        }
    }

    private void UpdateFrequencyAxis()
    {
        if (_freqAxisCanvas == null || Monitor == null) return;

        double minF = Monitor.MinFrequency;
        double maxF = Monitor.MaxFrequency;
        double containerHeight = _imageContainer?.Bounds.Height ?? 180;

        if (maxF <= minF || containerHeight <= 0)
        {
            foreach (var t in _freqTickLabels) t.IsVisible = false;
            return;
        }

        double textHeight = 13; // approximate height of a 10px font line

        for (int i = 0; i < FreqTickCount; i++)
        {
            double fraction = (double)i / (FreqTickCount - 1); // 0=top, 1=bottom
            double freq = maxF - fraction * (maxF - minF);

            // Position: clamp so labels don't clip at top/bottom
            double yCenter = fraction * containerHeight;
            double yPos = Math.Clamp(yCenter - textHeight / 2, 0, containerHeight - textHeight);

            var label = _freqTickLabels[i];
            label.IsVisible = true;
            label.Text = FormatFrequencyHz(freq);
            Canvas.SetTop(label, yPos);
        }
    }

    /// <summary>
    /// Format frequency with Hz/kHz suffix for readability.
    /// </summary>
    private static string FormatFrequencyHz(double hz)
    {
        if (hz >= 10000) return $"{hz / 1000:F0}kHz";
        if (hz >= 1000)  return $"{hz / 1000:F1}kHz";
        if (hz >= 100)   return $"{hz:F0}Hz";
        if (hz >= 10)    return $"{hz:F1}Hz";
        if (hz >= 1)     return $"{hz:F1}Hz";
        return $"{hz:F2}Hz";
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateFrequencyAxis();
    }

    #endregion
}