using System;
using System.Globalization;
using Avalonia.Data.Converters;

/// <summary>
/// Converts boolean to text for various UI elements.
/// </summary>
public class BoolToTextConverter : IValueConverter
{
    public static BoolToTextConverter FittedStatus { get; } = new BoolToTextConverter
    {
        TrueText = "FITTED",
        FalseText = "NOT FITTED"
    };

    public static BoolToTextConverter CollectToggle { get; } = new BoolToTextConverter
    {
        TrueText = "Stop Collecting",
        FalseText = "Start Collecting"
    };

    public string TrueText { get; set; } = "True";
    public string FalseText { get; set; } = "False";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue ? TrueText : FalseText;

        return FalseText;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}