using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.Analytics;

namespace MOSAIC.ViewModels.Analytics;

/// <summary>
/// ViewModel for the AmplitudeMetrics card.
/// </summary>
public partial class MetricsExtractorViewModel : ObservableObject
{
    public MetricsExtractor MetricsExtractor { get; }

    /// <summary>
    /// Available metric types for the ComboBox.
    /// </summary>
    public MetricType[] MetricTypes { get; } = Enum.GetValues<MetricType>();

    /// <summary>
    /// Gets the formula for the current metric type.
    /// </summary>
    public string Formula => MetricsExtractor.Metric switch
    {
        MetricType.RMS    => "√(Σx²/N)",
        MetricType.MAV    => "Σ|x|/N",
        MetricType.IEMG   => "Σ|x|",
        MetricType.SSI    => "Σx²",
        MetricType.VAR    => "Σ(x-μ)²/N",
        MetricType.STD    => "√(Σ(x-μ)²/N)",
        MetricType.LOG    => "e^(Σlog|x|/N)",
        MetricType.PEAK   => "max(|x|)",
        MetricType.P2P    => "max-min",
        MetricType.MEAN   => "Σx/N",
        MetricType.ZSCORE => "(x[N]-μ)/σ",
        _ => "?"
    };

    /// <summary>
    /// Gets the description for the current metric type.
    /// </summary>
    public string MetricDescription => MetricsExtractor.Metric switch
    {
        MetricType.RMS    => "Root Mean Square - signal power",
        MetricType.MAV    => "Mean Absolute Value - average amplitude",
        MetricType.IEMG   => "Integrated EMG - total activity",
        MetricType.SSI    => "Simple Square Integral - energy",
        MetricType.VAR    => "Variance - signal variability",
        MetricType.STD    => "Standard Deviation - spread",
        MetricType.LOG    => "Log Detector - geometric mean",
        MetricType.PEAK   => "Peak - maximum absolute value",
        MetricType.P2P    => "Peak-to-Peak - full range",
        MetricType.MEAN   => "Mean - average value",
        MetricType.ZSCORE => "Z-Score - normalized last sample relative to window",
        _ => ""
    };

    public MetricsExtractorViewModel(MetricsExtractor amplitudeMetrics)
    {
        MetricsExtractor = amplitudeMetrics;

        // Forward Metric changes to update computed properties
        MetricsExtractor.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MetricsExtractor.Metric))
            {
                OnPropertyChanged(nameof(Formula));
                OnPropertyChanged(nameof(MetricDescription));
            }
        };
    }
}