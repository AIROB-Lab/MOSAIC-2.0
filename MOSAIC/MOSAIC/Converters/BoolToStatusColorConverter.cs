using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MOSAIC.Converters;

/// <summary>
/// Maps a bool to a themed status brush, resolved from the active theme:
/// <c>true</c> → <c>StatusNormalBrush</c> (green), <c>false</c> → <c>StatusErrorBrush</c> (red).
/// Pass a resource key as <c>ConverterParameter</c> to override the false brush
/// (e.g. <c>ThemeBorderBrush</c> for a neutral/inactive state).
/// </summary>
public sealed class BoolToStatusColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "StatusNormalBrush" : parameter as string ?? "StatusErrorBrush";

        if (Application.Current is { } app &&
            app.TryGetResource(key, app.ActualThemeVariant, out var res) &&
            res is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}