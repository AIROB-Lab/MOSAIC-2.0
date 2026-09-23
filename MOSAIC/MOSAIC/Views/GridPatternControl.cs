using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MOSAIC.Views;

/// <summary>
/// A control that draws a dot grid pattern that adapts to the current theme.
/// Replaces the static SVG background with a dynamic, theme-aware grid.
/// </summary>
public class GridPatternControl : Control
{
    public static readonly StyledProperty<double> GridSizeProperty =
        AvaloniaProperty.Register<GridPatternControl, double>(nameof(GridSize), 40);

    public static readonly StyledProperty<double> DotSizeProperty =
        AvaloniaProperty.Register<GridPatternControl, double>(nameof(DotSize), 2);

    public static readonly StyledProperty<GridPatternStyle> PatternStyleProperty =
        AvaloniaProperty.Register<GridPatternControl, GridPatternStyle>(nameof(PatternStyle), GridPatternStyle.Dots);

    /// <summary>
    /// Size of the grid cells in pixels.
    /// </summary>
    public double GridSize
    {
        get => GetValue(GridSizeProperty);
        set => SetValue(GridSizeProperty, value);
    }

    /// <summary>
    /// Size of the dots (for Dots style) or line thickness (for Lines/Crosshairs style).
    /// </summary>
    public double DotSize
    {
        get => GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }

    /// <summary>
    /// Style of the grid pattern.
    /// </summary>
    public GridPatternStyle PatternStyle
    {
        get => GetValue(PatternStyleProperty);
        set => SetValue(PatternStyleProperty, value);
    }

    public GridPatternControl()
    {
        // Subscribe to theme changes
        App.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        App.ThemeChanged -= OnThemeChanged;
    }

    static GridPatternControl()
    {
        AffectsRender<GridPatternControl>(GridSizeProperty, DotSizeProperty, PatternStyleProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Get theme-aware color
        var gridBrush = GetGridBrush();
        var gridSize = GridSize;
        var dotSize = DotSize;

        switch (PatternStyle)
        {
            case GridPatternStyle.Dots:
                RenderDots(context, bounds, gridBrush, gridSize, dotSize);
                break;
            case GridPatternStyle.Lines:
                RenderLines(context, bounds, gridBrush, gridSize, dotSize);
                break;
            case GridPatternStyle.Crosshairs:
                RenderCrosshairs(context, bounds, gridBrush, gridSize, dotSize);
                break;
            case GridPatternStyle.None:
                // Don't render anything
                break;
        }
    }

    private IBrush GetGridBrush()
    {
        // Try to get from resources, fallback to theme-appropriate default
        if (this.TryFindResource("ThemeCanvasGridBrush", out var brush) && brush is IBrush b)
        {
            return b;
        }

        // Fallback based on current theme
        return App.CurrentTheme == AppTheme.Dark
            ? new SolidColorBrush(Color.Parse("#1E3A5F"))
            : new SolidColorBrush(Color.Parse("#CBD5E1"));
    }

    private void RenderDots(DrawingContext context, Rect bounds, IBrush brush, double gridSize, double dotSize)
    {
        var pen = new Pen(brush, 0);
        var radius = dotSize / 2;

        for (double x = gridSize; x < bounds.Width; x += gridSize)
        {
            for (double y = gridSize; y < bounds.Height; y += gridSize)
            {
                context.DrawEllipse(brush, pen, new Point(x, y), radius, radius);
            }
        }
    }

    private void RenderLines(DrawingContext context, Rect bounds, IBrush brush, double gridSize, double thickness)
    {
        var pen = new Pen(brush, thickness);

        // Vertical lines
        for (double x = gridSize; x < bounds.Width; x += gridSize)
        {
            context.DrawLine(pen, new Point(x, 0), new Point(x, bounds.Height));
        }

        // Horizontal lines
        for (double y = gridSize; y < bounds.Height; y += gridSize)
        {
            context.DrawLine(pen, new Point(0, y), new Point(bounds.Width, y));
        }
    }

    private void RenderCrosshairs(DrawingContext context, Rect bounds, IBrush brush, double gridSize, double thickness)
    {
        var pen = new Pen(brush, thickness);
        var crossSize = gridSize * 0.15; // 15% of grid size

        for (double x = gridSize; x < bounds.Width; x += gridSize)
        {
            for (double y = gridSize; y < bounds.Height; y += gridSize)
            {
                // Horizontal part of crosshair
                context.DrawLine(pen, new Point(x - crossSize, y), new Point(x + crossSize, y));
                // Vertical part of crosshair
                context.DrawLine(pen, new Point(x, y - crossSize), new Point(x, y + crossSize));
            }
        }
    }
}

/// <summary>
/// Available grid pattern styles.
/// </summary>
public enum GridPatternStyle
{
    /// <summary>Simple dots at grid intersections.</summary>
    Dots,
    
    /// <summary>Full grid lines.</summary>
    Lines,
    
    /// <summary>Small crosshairs at grid intersections.</summary>
    Crosshairs,
    
    /// <summary>No grid pattern (blank background).</summary>
    None
}
