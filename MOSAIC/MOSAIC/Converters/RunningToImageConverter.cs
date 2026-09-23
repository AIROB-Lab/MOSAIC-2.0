// RunningToImageConverter.cs
using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MOSAIC.Converters;

public sealed class RunningToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var running = value as bool? ?? false;
        var uri = running
            ? new Uri("avares://MOSAIC/Assets/stop.png")
            : new Uri("avares://MOSAIC/Assets/start.png");

        // Return Bitmap so it works on all bindings
        using var stream = AssetLoader.Open(uri);
        return new Bitmap(stream);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}