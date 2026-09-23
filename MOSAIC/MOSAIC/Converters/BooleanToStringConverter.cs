using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MOSAIC.Converters;

public sealed class BooleanToStringConverter : IValueConverter
{
    // ConverterParameter: "TrueText|FalseText"
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var texts = (parameter as string)?.Split('|');
        var t = texts is { Length: 2 } ? texts[0] : "Hide Details";
        var f = texts is { Length: 2 } ? texts[1] : "Show Details";
        return value is true ? t : f;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}