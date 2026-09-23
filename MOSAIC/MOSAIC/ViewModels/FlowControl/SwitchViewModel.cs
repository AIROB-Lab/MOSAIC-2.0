using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.ViewModels.FlowControl;

public partial class SwitchViewModel : ObservableObject
{
    public Switch Switch { get; }

    public SwitchViewModel(Switch switchBlock)
    {
        Switch = switchBlock;
        Switch.InputSwitched += OnInputSwitched;
    }

    private void OnInputSwitched(bool useSecondInput)
    {
        OnPropertyChanged(nameof(UseSecondInput));
        OnPropertyChanged(nameof(ActiveInputDisplay));
        OnPropertyChanged(nameof(Input1BackgroundBrush));
        OnPropertyChanged(nameof(Input1ForegroundBrush));
        OnPropertyChanged(nameof(Input1BorderBrush));
        OnPropertyChanged(nameof(Input2BackgroundBrush));
        OnPropertyChanged(nameof(Input2ForegroundBrush));
        OnPropertyChanged(nameof(Input2BorderBrush));
    }

    public bool UseSecondInput
    {
        get => Switch.UseSecondInput;
        set
        {
            if (Switch.UseSecondInput != value)
            {
                Switch.UseSecondInput = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveInputDisplay));
            }
        }
    }

    public string ActiveInputDisplay => UseSecondInput ? "Input 2" : "Input 1";

    #region Input 1 Styling

    public IBrush Input1BackgroundBrush => !UseSecondInput
        ? GetResourceBrush("CategoryFlowControlBrush", "#10B981")
        : GetResourceBrush("ThemeInputBackgroundBrush", "#1A1A1A");

    public IBrush Input1ForegroundBrush => !UseSecondInput
        ? new SolidColorBrush(Colors.White)
        : GetResourceBrush("ThemeTextSecondaryBrush", "#B0B0B0");

    public IBrush Input1BorderBrush => !UseSecondInput
        ? GetResourceBrush("CategoryFlowControlBrush", "#10B981")
        : new SolidColorBrush(Colors.Transparent);

    #endregion

    #region Input 2 Styling

    public IBrush Input2BackgroundBrush => UseSecondInput
        ? GetResourceBrush("CategoryFlowControlBrush", "#10B981")
        : GetResourceBrush("ThemeInputBackgroundBrush", "#1A1A1A");

    public IBrush Input2ForegroundBrush => UseSecondInput
        ? new SolidColorBrush(Colors.White)
        : GetResourceBrush("ThemeTextSecondaryBrush", "#B0B0B0");

    public IBrush Input2BorderBrush => UseSecondInput
        ? GetResourceBrush("CategoryFlowControlBrush", "#10B981")
        : new SolidColorBrush(Colors.Transparent);

    #endregion

    #region Commands

    [RelayCommand]
    private void Toggle()
    {
        Switch.Toggle();
    }

    [RelayCommand]
    private void SelectInput1()
    {
        Switch.SetActiveInput(0);
    }

    [RelayCommand]
    private void SelectInput2()
    {
        Switch.SetActiveInput(1);
    }

    #endregion

    #region Helpers

    private static IBrush GetResourceBrush(string resourceName, string fallbackColor)
    {
        if (Application.Current?.TryFindResource(resourceName, out var resource) == true && resource is IBrush brush)
            return brush;
        return new SolidColorBrush(Color.Parse(fallbackColor));
    }

    #endregion
}