using Avalonia;
using Avalonia.Controls;

namespace MOSAIC.Controls;

/// <summary>
/// Reusable "label · bar · degrees" row for DOF / joint-angle visualisation. The bar fills to
/// <see cref="Value"/> / <see cref="Max"/>; the read-out shows the angle in degrees (F0).
/// </summary>
public sealed partial class JointAngleRow : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<JointAngleRow, string>(nameof(Label), "");

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<JointAngleRow, double>(nameof(Value));

    public static readonly StyledProperty<double> MaxProperty =
        AvaloniaProperty.Register<JointAngleRow, double>(nameof(Max), 100d);

    /// <summary>
    /// Lower bound of the bar. Defaults to 0, so existing unipolar consumers are unaffected;
    /// set it to a negative value for a bipolar quantity such as a joint angle in −180…180,
    /// where a 0-based bar renders every negative angle as an identical empty bar.
    /// </summary>
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<JointAngleRow, double>(nameof(Minimum));

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

    public double Max
    {
        get => GetValue(MaxProperty);
        set => SetValue(MaxProperty, value);
    }

    /// <inheritdoc cref="MinimumProperty"/>
    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public JointAngleRow() => InitializeComponent();
}
