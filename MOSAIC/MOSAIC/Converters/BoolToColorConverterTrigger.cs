using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MOSAIC.Converters;

/// <summary>
/// Converts bool to color. Parameter format: "TrueColor|FalseColor" (hex colors)
/// Example: ConverterParameter="#FF0000|#00FF00"
/// </summary>
public class BoolToColorConverterTrigger : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue || parameter is not string paramStr)
            return Brushes.Transparent;

        var colors = paramStr.Split('|');
        if (colors.Length != 2)
            return Brushes.Transparent;

        var colorStr = boolValue ? colors[0] : colors[1];
        
        try
        {
            if (Color.TryParse(colorStr, out var color))
            {
                return new SolidColorBrush(color);
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return Brushes.Transparent;
    }
    

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}