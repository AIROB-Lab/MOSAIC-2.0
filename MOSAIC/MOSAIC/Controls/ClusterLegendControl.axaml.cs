using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace MOSAIC.Controls;

/// <summary>
/// Reusable cluster/class legend: a wrap of "label (count)" chips, each with a delete (✕) button.
/// Host binds <see cref="ItemsSource"/> to a ClusterInfo collection and <see cref="ClearClusterCommand"/>
/// to a command taking the chip's label as parameter.
/// </summary>
public sealed partial class ClusterLegendControl : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ClusterLegendControl, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<ICommand?> ClearClusterCommandProperty =
        AvaloniaProperty.Register<ClusterLegendControl, ICommand?>(nameof(ClearClusterCommand));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand? ClearClusterCommand
    {
        get => GetValue(ClearClusterCommandProperty);
        set => SetValue(ClearClusterCommandProperty, value);
    }

    public ClusterLegendControl() => InitializeComponent();
}
