using Avalonia;
using Avalonia.Media;
using MOSAIC.Services;
using SkiaSharp;

namespace MOSAIC.Visualization;

/// <summary>
/// Central theme color provider for all visualization components.
///
/// Two categories:
/// 1. Avalonia controls — use DynamicResourceExtension with the string constants:
///    <code>control[!Border.BackgroundProperty] = new DynamicResourceExtension(ThemeHelper.ChartBackgroundBrush);</code>
/// 2. SkiaSharp/LiveCharts paints — use the static SKColor properties directly:
///    <code>var paint = new SolidColorPaint(ThemeHelper.ChartAxisLabel);</code>
/// </summary>
public static class ThemeHelper
{
    #region DynamicResource brushes

    // Use: control[!prop] = new DynamicResourceExtension(key)
    // These auto-update on theme switch — no event handler needed.


    public const string ChartBackgroundBrush = "ThemeChartBackgroundBrush";
    public const string TextPrimaryBrush     = "ThemeTextPrimaryBrush";
    public const string TextSecondaryBrush   = "ThemeTextSecondaryBrush";
    public const string TextDimmedBrush      = "ThemeTextDimmedBrush";
    public const string SectionBrush         = "ThemeSectionBrush";
    public const string BorderBrush          = "ThemeBorderBrush";
    public const string AccentBrush          = "ThemeAccentBrush";

    public static IBrush GetBrush(string resourceKey)
    {
        if (Application.Current?.TryGetResource(resourceKey, Application.Current.ActualThemeVariant, out var value) == true
            && value is IBrush brush)
            return brush;
        return Brushes.Transparent;
    }

    #endregion

    #region Theme colors

    // These read App.CurrentTheme on every access so they
    // always return the correct color for the active theme.
    // Call from UpdateThemeColors() or InitializeAxes().


    private static bool IsDark => App.CurrentTheme == AppTheme.Dark;

    /// <summary>Chart background as Avalonia Color.</summary>
    public static Color ChartBackground =>
        IsDark ? Color.Parse("#0A0C0F") : Colors.White;

    /// <summary>Axis label paint color — full opacity for readability.</summary>
    public static SKColor ChartAxisLabel =>
        IsDark ? new SKColor(230, 230, 230) : new SKColor(15, 23, 42);

    /// <summary>Grid line paint color.</summary>
    public static SKColor ChartGrid =>
        IsDark ? new SKColor(255, 255, 255, 80) : new SKColor(107, 114, 128, 100);

    /// <summary>Zero line paint color (scope only).</summary>
    public static SKColor ChartZeroLine =>
        IsDark ? new SKColor(255, 255, 255, 120) : new SKColor(75, 85, 99, 150);

    /// <summary>Tick mark paint color.</summary>
    public static SKColor ChartTick =>
        IsDark ? new SKColor(255, 255, 255, 140) : new SKColor(107, 114, 128, 180);

    #endregion
}