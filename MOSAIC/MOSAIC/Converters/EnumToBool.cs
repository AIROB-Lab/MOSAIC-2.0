using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MOSAIC.Converters;

/// <summary>
/// Converts an enum value to <see langword="true"/> when it equals the
/// converter parameter (the parameter is a string parsed against the bound
/// enum's type). The reverse direction returns the parsed enum value when
/// fed <see langword="true"/>, and <see cref="Avalonia.Data.BindingOperations.DoNothing"/>
/// otherwise — which is what makes a group of <c>RadioButton</c>s correctly
/// act as a single-selection over an enum-valued property:
/// <code>
/// IsChecked="{Binding Source, Mode=TwoWay,
///                     Converter={x:Static converters:EnumToBoolConverter.Instance},
///                     ConverterParameter=Manual}"
/// </code>
/// Each radio's IsChecked turns into "Source == Manual"; flipping a radio on
/// emits "Manual" back into the binding, which selects that radio and
/// deselects the others (because the others now bind to false).
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    /// <summary>Shared singleton — used as <c>{x:Static converters:EnumToBoolConverter.Instance}</c>.</summary>
    public static readonly EnumToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        var enumString = parameter.ToString();
        if (string.IsNullOrEmpty(enumString)) return false;
        return string.Equals(value.ToString(), enumString, StringComparison.Ordinal);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool b || !b || parameter is null)
            return Avalonia.Data.BindingOperations.DoNothing;

        var enumString = parameter.ToString();
        if (string.IsNullOrEmpty(enumString))
            return Avalonia.Data.BindingOperations.DoNothing;

        try
        {
            return Enum.Parse(targetType, enumString, ignoreCase: true);
        }
        catch
        {
            return Avalonia.Data.BindingOperations.DoNothing;
        }
    }
}