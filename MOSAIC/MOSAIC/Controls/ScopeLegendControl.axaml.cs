using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace MOSAIC.Controls;

/// <summary>
/// Reusable scope/channel legend: a wrap of "● label" chips. Host binds <see cref="ItemsSource"/>
/// to a collection of <c>ScopeLegendItem(Label, Color)</c> (the colour is a hex string).
/// </summary>
public sealed partial class ScopeLegendControl : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ScopeLegendControl, IEnumerable?>(nameof(ItemsSource));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ScopeLegendControl() => InitializeComponent();
}
