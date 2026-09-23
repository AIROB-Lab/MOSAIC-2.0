using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MOSAIC.Components.Factory;

namespace MOSAIC.Converters;

/// <summary>
/// Maps a <see cref="BlockCategory"/> to its themed category brush from MosaicColors — the same
/// palette the block cards use — so the palette's colour stripes/dots stay consistent with the
/// rest of the UI. Resolves named brushes rather than hardcoding hex (per the design system).
/// </summary>
public sealed class BlockCategoryToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is BlockCategory cat
            ? cat switch
            {
                BlockCategory.Analytics        => "CategoryAnalyticsBrush",
                BlockCategory.Devices          => "CategoryDevicesBrush",
                BlockCategory.FlowControl      => "CategoryFlowControlBrush",
                BlockCategory.MachineLearning  => "CategoryMachineLearningBrush",
                BlockCategory.SignalProcessing => "CategorySignalProcessingBrush",
                BlockCategory.Streaming        => "StreamingSourcesBrush",
                BlockCategory.Tests            => "CategoryTestsBrush",
                _                              => null
            }
            : null;

        if (key is not null &&
            Application.Current is { } app &&
            app.TryGetResource(key, app.ActualThemeVariant, out var res) &&
            res is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
