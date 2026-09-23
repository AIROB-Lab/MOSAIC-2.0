using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MOSAIC.Converters;

/// <summary>
/// Converts boolean to opacity (1.0 or 0.4).
/// </summary>
public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? 1.0 : 0.4;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}