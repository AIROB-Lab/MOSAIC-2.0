using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MOSAIC.Converters;

/// <summary>
/// Converts a <see cref="bool"/> to one of two <see cref="double"/> values.
/// Used in AXAML to bind <c>IsBipolar</c> → ProgressBar <c>Minimum</c>.
/// </summary>
/// <example>
/// <code>
/// &lt;converters:BoolToDoubleConverter x:Key="BipolarToMin" TrueValue="-1" FalseValue="0"/&gt;
/// &lt;ProgressBar Minimum="{Binding IsBipolar, Converter={StaticResource BipolarToMin}}"/&gt;
/// </code>
/// </example>
public class BoolToDoubleConverter : IValueConverter
{
    /// <summary>Value returned when the input is <c>true</c>.</summary>
    public double TrueValue { get; set; } = 1.0;

    /// <summary>Value returned when the input is <c>false</c>.</summary>
    public double FalseValue { get; set; } = 0.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueValue : FalseValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}