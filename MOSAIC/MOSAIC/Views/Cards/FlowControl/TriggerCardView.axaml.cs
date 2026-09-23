using Avalonia.Controls;
using MOSAIC.Views.Cards;

namespace MOSAIC.Views.Cards.FlowControl;

public partial class TriggerCardView : PopoutCardBase
{
    public TriggerCardView()
    {
        InitializeComponent();
    }

    protected override Control CreateConfigView() => new TriggerConfigView();
}
