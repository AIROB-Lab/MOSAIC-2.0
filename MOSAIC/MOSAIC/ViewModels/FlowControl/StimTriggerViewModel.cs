using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.ViewModels.FlowControl;

public partial class StimTriggerViewModel : ObservableObject
{
    private readonly StimTrigger _stimTrigger;

    public StimTriggerViewModel(StimTrigger stimTrigger)
    {
        _stimTrigger = stimTrigger;
    }

    public StimTrigger StimTrigger => _stimTrigger;

    /// <summary>
    /// Indicates whether the trigger is currently in capturing state.
    /// </summary>
    public bool IsCapturing => _stimTrigger.IsCapturing;

    /// <summary>
    /// Text description of the current capture state.
    /// </summary>
    public string CaptureStateText => IsCapturing ? "Active - Capturing" : "Idle - Waiting";
}