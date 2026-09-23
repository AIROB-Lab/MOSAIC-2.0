using Avalonia.Controls;

namespace MOSAIC.Views.Cards.MachineLearning;

public partial class IncrementalPredictorView : PopoutCardBase
{
    public IncrementalPredictorView()
    {
        InitializeComponent();
    }

    protected override Control CreateConfigView() => new IncrementalPredictorConfigView();
}
