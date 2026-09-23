using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MOSAIC.Converters;

/// <summary>
/// Converts a <see cref="bool"/> to one of two strings.
/// </summary>
/// <example>
/// <code>
/// &lt;converters:BoolToStringConverter TrueValue="Recording…" FalseValue="Idle"/&gt;
/// </code>
/// </example>
public class BoolToStringConverter : IValueConverter
{
    /// <summary>String returned when the value is <see langword="true"/>.</summary>
    public string TrueValue  { get; set; } = "True";

    /// <summary>String returned when the value is <see langword="false"/>.</summary>
    public string FalseValue { get; set; } = "False";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueValue : FalseValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == TrueValue;
}

/// <summary>
/// Converts an integer count to a <see cref="bool"/> indicating whether it is zero.
/// Use <see cref="IsZero"/> as a static instance for inline XAML binding.
/// </summary>
/// <example>
/// Hide an element when count is zero:
/// <code>
/// &lt;Border IsVisible="{Binding SegmentCount,
///     Converter={x:Static converters:CountToVisibilityConverter.IsZero},
///     ConverterParameter=Invert}"/&gt;
/// </code>
/// Show an element only when count is zero (no parameter):
/// <code>
/// &lt;Border IsVisible="{Binding SegmentCount,
///     Converter={x:Static converters:CountToVisibilityConverter.IsZero}}"/&gt;
/// </code>
/// </example>
public class CountToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Singleton instance. Returns <see langword="true"/> when count is zero
    /// (i.e. shows the empty-state element). Pass <c>Invert</c> as
    /// <c>ConverterParameter</c> to reverse the logic.
    /// </summary>
    public static readonly CountToVisibilityConverter IsZero = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isZero = value is int i && i == 0;
        var invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        return invert ? !isZero : isZero;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}