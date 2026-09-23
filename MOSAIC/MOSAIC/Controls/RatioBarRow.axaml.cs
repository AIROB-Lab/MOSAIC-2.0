using Avalonia;
using Avalonia.Controls;

namespace MOSAIC.Controls;

/// <summary>
/// Reusable "label · proportional bar · percent" row for displaying a 0..1 ratio
/// (e.g. a principal component's explained-variance share). Themed to the analytics accent.
/// </summary>
public sealed partial class RatioBarRow : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<RatioBarRow, string>(nameof(Label), "");

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RatioBarRow, double>(nameof(Value));

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The ratio to display, in the range [0, 1].</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public RatioBarRow() => InitializeComponent();
}
