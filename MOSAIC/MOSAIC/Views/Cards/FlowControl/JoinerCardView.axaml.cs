using Avalonia.Controls;

namespace MOSAIC.Views.Cards;

public partial class JoinerCardView : PopoutCardBase
{
    public JoinerCardView()
    {
        InitializeComponent();
    }

    protected override Control CreateConfigView() => new JoinerConfigView();
}
