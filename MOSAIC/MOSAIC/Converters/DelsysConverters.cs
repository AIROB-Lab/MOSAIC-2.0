using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MOSAIC.Converters;

/// <summary>
/// Converts a boolean (IsStreaming) to button text: false → "Stream", true → "Stop".
/// </summary>
public class BoolToStreamTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Stop" : "Stream";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a hex color string (e.g. "#FF4136") to an Avalonia <see cref="SolidColorBrush"/>.
/// Used for scope legend dots that need to match trace colors.
/// </summary>
public class StringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && Color.TryParse(hex, out var color))
            return new SolidColorBrush(color);
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
