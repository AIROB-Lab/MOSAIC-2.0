using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.ViewModels.FlowControl;

/// <summary>
/// ViewModel for the MeanAverageValue card.
/// </summary>
public partial class MeanAverageValueViewModel(MeanAverageValue meanAverageValue) : ObservableObject
{
    public MeanAverageValue MeanAverageValue => meanAverageValue;
}