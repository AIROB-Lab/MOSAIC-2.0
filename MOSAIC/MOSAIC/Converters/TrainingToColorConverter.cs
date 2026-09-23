using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MOSAIC.Converters;

/// <summary>
/// Converts training state to appropriate button color.
/// </summary>
public class TrainingToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isTraining = value is true;
        return isTraining
            ? new SolidColorBrush(Color.Parse("#C62828"))  // Red when training (to stop)
            : new SolidColorBrush(Color.Parse("#2E7D32")); // Green when not (to start)
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}