using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Components;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Interfaces;
using ClockBlock = MOSAIC.Models.FlowControl.ClockBlock;

namespace MOSAIC.ViewModels;

public partial class ClockViewModel(ClockBlock clock) : ObservableObject
{
    public ClockBlock Clock => clock;
    [ObservableProperty] private bool running;

    [RelayCommand]
    private void Start()
    {
        Clock.Start();
        Running = Clock.Running;
    }

    [RelayCommand]
    private void Stop()
    {
        Clock.Stop();
        Running = Clock.Running;
    }

    [RelayCommand]
    private void Toggle()
    {
        if (Running) Stop();
        else Start();
    }
}