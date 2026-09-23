using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MOSAIC.Converters;

/// <summary>
/// Maps a free-form device/pipeline status to a themed status brush, by keyword. Accepts either a
/// status STRING (e.g. "Idle", "Streaming", "Disconnected", "Waiting for probe...") or a status
/// ENUM (its name is used — e.g. HannesHand's <c>DeviceStatus.Ready</c>/<c>DongleReady</c>/<c>Error</c>).
/// Use for status badges whose source is not the <c>BlockStatus</c> enum (which has
/// <see cref="StatusToColorConverter"/>). Order matters: error keywords are checked first, then the
/// transient/amber set before the green set so "Disconnected", "Connecting" and "DongleReady" do not
/// read as a healthy connection.
/// </summary>
public class StatusTextToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value?.ToString() ?? string.Empty).ToLowerInvariant();

        string key;
        if (text.Contains("disconnect") || text.Contains("error") || text.Contains("fail") ||
            text.Contains("lost") || text.Contains("stopped") || text.Contains("fault"))
            key = "StatusErrorBrush";                       // red — fault / not connected
        else if (text.Contains("wait") || text.Contains("connecting") || text.Contains("starting") ||
                 text.Contains("pairing") || text.Contains("dongle") || text.Contains("init"))
            key = "StatusWarningBrush";                     // amber — transient working state
        else if (text.Contains("stream") || text.Contains("running") || text.Contains("connected") ||
                 text.Contains("active") || text.Contains("ready") || text.Contains("live") ||
                 text.Contains("online"))
            key = "StatusNormalBrush";                      // green — healthy, flowing
        else
            key = "StatusIdleBrush";                        // grey — idle / unknown

        var brush = ResolveBrush(key);

        // Border.Background wants an IBrush; some targets want a Color.
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
