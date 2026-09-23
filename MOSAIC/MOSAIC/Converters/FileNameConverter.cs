using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;

namespace MOSAIC.Converters
{
    /// <summary>
    /// Shows a path as just its file name, leaving the bound value itself untouched.
    /// </summary>
    /// <remarks>
    /// For lists whose items must stay full paths because something acts on them — the BodyRig
    /// card's calibration profiles are selected as paths and handed straight to load and store —
    /// while a full Windows path in a ~440px card pushes the only distinguishing part, the file
    /// name, out of view.
    /// </remarks>
    public sealed class FileNameConverter : IValueConverter
    {
        /// <summary>Shared instance for direct XAML reference.</summary>
        public static readonly FileNameConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string path || path.Length == 0) return value;

            // GetFileName returns empty for a trailing separator; show the original rather than
            // a blank row, which would look like a corrupt entry.
            var name = Path.GetFileName(path);
            return name.Length > 0 ? name : path;
        }

        /// <summary>Not supported: a file name cannot be resolved back to a path.</summary>
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Avalonia.Data.BindingOperations.DoNothing;
    }
}
