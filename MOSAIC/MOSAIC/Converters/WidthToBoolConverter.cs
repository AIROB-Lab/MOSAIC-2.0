using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace MOSAIC.Converters
{
    /// <summary>
    /// Returns true when the width (or Size) is less than the specified Breakpoint.
    /// Useful for responsive “IsNarrow” bindings in Avalonia.
    /// </summary>
    public class WidthToBoolConverter : IValueConverter
    {
        /// <summary>
        /// Pixel width at which the converter starts returning true.
        /// </summary>
        public double Breakpoint { get; set; } = 900;

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            // value may come as a double (Bounds.Width) or Avalonia.Size
            double width = value switch
            {
                double w => w,
                Size s   => s.Width,
                _        => 0
            };
            return width < Breakpoint;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Avalonia.Data.BindingOperations.DoNothing;
    }
}