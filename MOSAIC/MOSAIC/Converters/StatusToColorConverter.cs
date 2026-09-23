using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MOSAIC.Components.Enums;

namespace MOSAIC.Converters;

/// <summary>
/// Maps a <see cref="BlockStatus"/> to a themed status brush (or its colour, for
/// targets that expect a <see cref="Color"/>). Resolves the shared status tokens
/// from MosaicColors so the palette lives in one place. Every enum value is mapped —
/// no status falls through to a transparent / invisible badge.
/// </summary>
public class StatusToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Collapse the six BlockStatus values into the four design-system buckets.
        var key = value switch
        {
            BlockStatus.Normal    => "StatusNormalBrush",   // healthy, keeping up
            BlockStatus.Stable    => "StatusNormalBrush",   // healthy steady state
            BlockStatus.Learning  => "StatusWarningBrush",  // transient working state
            BlockStatus.Stumbling => "StatusWarningBrush",  // short-term irregular, recoverable
            BlockStatus.Lagging   => "StatusErrorBrush",    // falling behind
            BlockStatus.Idle      => "StatusIdleBrush",     // no activity
            _                     => "StatusIdleBrush"      // unknown → neutral, never invisible
        };

        var brush = ResolveBrush(key);

        // Border.Background / Ellipse.Fill want an IBrush; some bindings want a Color.
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