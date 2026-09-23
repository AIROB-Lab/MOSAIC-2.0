using CommunityToolkit.Mvvm.ComponentModel;
using FlowControlModels = MOSAIC.Models.FlowControl;

namespace MOSAIC.ViewModels.FlowControl;

public class SelectorViewModel(FlowControlModels.Selector selector) : ObservableObject
{
    public FlowControlModels.Selector Selector => selector;
}
