using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.ViewModels.FlowControl;

/// <summary>
/// ViewModel for the <see cref="ManualControl"/> card.
/// </summary>
public partial class ManualControlViewModel : ObservableObject
{
    public ManualControl ManualControl { get; }

    public IRelayCommand ResetCommand   { get; }
    public IRelayCommand NeutralCommand { get; }

    public ManualControlViewModel(ManualControl manualControl)
    {
        ManualControl = manualControl;

        ResetCommand   = new RelayCommand(() => ManualControl.Reset());
        NeutralCommand = new RelayCommand(() => ManualControl.Neutral());
    }
}