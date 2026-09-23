using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MOSAIC.Converters;

/// <summary>
/// Converts a boolean IsStreaming value to the toggle button label.
/// </summary>
public class StreamButtonConverter : IValueConverter
{
    public static readonly StreamButtonConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Stop" : "Stream";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}