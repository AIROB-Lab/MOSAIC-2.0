using CommunityToolkit.Mvvm.ComponentModel;
using AdaptiveFilter = MOSAIC.Models.AdaptiveFilterBlock;


namespace MOSAIC.ViewModels.SignalProcessing
{
    public class AdaptiveFilterViewModel(AdaptiveFilter adaptiveFilter): ObservableObject
    {
        public AdaptiveFilter AdaptiveFilter => adaptiveFilter;
    }
}
