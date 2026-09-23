using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace MOSAIC.Controls;

/// <summary>
/// Reusable "label · numeric up/down" parameter row. Consumers bind <see cref="Value"/>
/// two-way to an integer view-model property; the field exposes spinner arrows and direct entry.
/// </summary>
public sealed partial class NumericParameterRow : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<NumericParameterRow, string>(nameof(Label), "");

    public static readonly StyledProperty<int> MinimumProperty =
        AvaloniaProperty.Register<NumericParameterRow, int>(nameof(Minimum));

    public static readonly StyledProperty<int> MaximumProperty =
        AvaloniaProperty.Register<NumericParameterRow, int>(nameof(Maximum), 100);

    public static readonly StyledProperty<int> IncrementProperty =
        AvaloniaProperty.Register<NumericParameterRow, int>(nameof(Increment), 1);

    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<NumericParameterRow, int>(
            nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public int Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Increment
    {
        get => GetValue(IncrementProperty);
        set => SetValue(IncrementProperty, value);
    }

    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public NumericParameterRow() => InitializeComponent();
}
