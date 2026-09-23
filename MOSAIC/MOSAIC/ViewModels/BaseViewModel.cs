using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Components.Basics;

namespace MOSAIC.ViewModels;

public class BaseViewModel(BaseBlock baseBlock) : ObservableObject
{
    public BaseBlock BaseBlock => baseBlock;
}