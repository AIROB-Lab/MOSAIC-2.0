using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MOSAIC.Diagnostics;

namespace MOSAIC.Converters;

/// <summary>
/// Maps a <see cref="LogLevel"/> to a themed status brush (or its colour, for targets that expect a
/// <see cref="Color"/>), so the log panel's severity dot uses the same four tokens as every other
/// status indicator in the app.
/// </summary>
/// <remarks>
/// Modelled on <see cref="StatusToColorConverter"/>: the key is resolved through the application's
/// resources rather than baked in, so swapping the theme dictionary at runtime still recolours it.
/// </remarks>
public class LogLevelToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            LogLevel.Error => "StatusErrorBrush",
            LogLevel.Warn  => "StatusWarningBrush",
            LogLevel.Info  => "StatusNormalBrush",
            _              => "StatusIdleBrush"   // Debug and anything unknown → neutral, never invisible
        };

        var brush = ResolveBrush(key);

        return typeof(IBrush).IsAssignableFrom(targetType) ? brush : brush.Color;
    }

    private static ISolidColorBrush ResolveBrush(string key)
    {
        if (Application.Current is { } app &&
            app.TryGetResource(key, app.ActualThemeVariant, out var res) &&
            res is ISolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.Gray);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
