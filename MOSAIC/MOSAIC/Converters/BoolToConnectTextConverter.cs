using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Data;

namespace MOSAIC.Converters
{
    public sealed class BoolToConnectTextConverter : IValueConverter
    {
        public static BoolToConnectTextConverter Instance { get; } = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool b && b ? "Disconnect" : "Connect";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => BindingOperations.DoNothing;
    }
}