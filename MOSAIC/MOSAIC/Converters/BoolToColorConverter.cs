using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MOSAIC.Converters;

/// <summary>
/// Converts boolean to color for various UI elements.
/// </summary>
public class BoolToColorConverter : IValueConverter
{
    public static BoolToColorConverter FittedColor { get; } = new BoolToColorConverter
    {
        TrueColor = "#00FF88",
        FalseColor = "#FF6B6B"
    };

    public static BoolToColorConverter CollectColor { get; } = new BoolToColorConverter
    {
        TrueColor = "#FF6B6B",
        FalseColor = "#9C27B0"
    };

    public string TrueColor { get; set; } = "#00FF00";
    public string FalseColor { get; set; } = "#FF0000";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return new SolidColorBrush(Color.Parse(boolValue ? TrueColor : FalseColor));

        return new SolidColorBrush(Color.Parse(FalseColor));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}