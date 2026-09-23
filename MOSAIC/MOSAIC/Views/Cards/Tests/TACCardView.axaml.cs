using Avalonia.Controls;
using MOSAIC.Views.Cards;

namespace MOSAIC.Views.Cards.Tests;

public partial class TACCardView : PopoutCardBase
{
    public TACCardView()
    {
        InitializeComponent();
    }

    protected override Control CreateConfigView() => new TACConfigView();
}
