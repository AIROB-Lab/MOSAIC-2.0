using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MOSAIC.Converters
{
    public sealed class BooleanInverseConverter : IValueConverter
    {
        // Provide a static instance you can reference from XAML
        public static readonly BooleanInverseConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool b ? !b : Avalonia.Data.BindingOperations.DoNothing;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool b ? !b : Avalonia.Data.BindingOperations.DoNothing;
    }
}