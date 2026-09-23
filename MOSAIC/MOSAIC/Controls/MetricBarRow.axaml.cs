using System;
using Avalonia;
using Avalonia.Controls;

namespace MOSAIC.Controls;

/// <summary>
/// Reusable "label · proportional bar · value" row for an unbounded metric (e.g. component
/// kurtosis, class separability). The bar fills to <c>|Value| / Max</c> (relative magnitude);
/// the number shows the raw value formatted with F2.
/// </summary>
public sealed partial class MetricBarRow : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<MetricBarRow, string>(nameof(Label), "");

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<MetricBarRow, double>(nameof(Value));

    public static readonly StyledProperty<double> MaxProperty =
        AvaloniaProperty.Register<MetricBarRow, double>(nameof(Max), 1d);

    /// <summary>Computed bar fill in [0, 1]; not set directly by consumers.</summary>
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<MetricBarRow, double>(nameof(Fraction));

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Normalisation denominator (typically the max magnitude across the shown metrics).</summary>
    public double Max
    {
        get => GetValue(MaxProperty);
        set => SetValue(MaxProperty, value);
    }

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public MetricBarRow() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MaxProperty)
        {
            var max = Max;
            Fraction = max > 0 ? Math.Clamp(Math.Abs(Value) / max, 0, 1) : 0;
        }
    }
}
