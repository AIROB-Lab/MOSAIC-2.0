using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MOSAIC.ViewModels.Devices;

namespace MOSAIC.Views.Cards.Devices;

public partial class HannesHandCardView : PopoutCardBase
{
    public HannesHandCardView()
    {
        InitializeComponent();
        
        // Register handlers with Tunnel strategy to catch events before controls handle them
        AddHandler(PointerPressedEvent, Velocity_PointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, Velocity_PointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, Velocity_PointerCaptureLost, RoutingStrategies.Tunnel);
    }

    private HannesHandViewModel? ViewModel => DataContext as HannesHandViewModel;

    #region Toggle Switch Event Handling
    
    /// <summary>
    /// Stops pointer events on toggle switches from bubbling up to parent Expander.
    /// This prevents the Expander from closing when clicking a toggle switch.
    /// </summary>
    private void ToggleSwitch_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Mark as handled to stop bubbling to Expander
        e.Handled = true;
    }

    private void ToggleSwitch_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Mark as handled to stop bubbling to Expander
        e.Handled = true;
    }

    #endregion

    #region Desktop Velocity Sliders (snap back to center)

    private void VelocitySlider_Released(object? sender, PointerReleasedEventArgs e)
    {
        SnapSliderToCenter(sender);
    }

    private void VelocitySlider_CaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        SnapSliderToCenter(sender);
    }

    private void SnapSliderToCenter(object? sender)
    {
        if (ViewModel is null) return;

        if (sender is Slider slider)
        {
            if (slider.Name == "WristPsSlider")
            {
                ViewModel.WristPsValue = 0;
            }
        }
    }

    #endregion

    #region Velocity Buttons (press & hold) - Using Tunnel routing

    private void Velocity_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Find the button from the event source
        var button = (e.Source as Control)?.FindAncestorOfType<Button>();
        if (button?.Tag is null || ViewModel is null)
            return;

        var tag = button.Tag.ToString();
        
        // Only handle velocity buttons
        if (tag != "WristPs_Neg" && tag != "WristPs_Pos" && 
            tag != "Thumb_Neg" && tag != "Thumb_Pos")
            return;
            
        System.Diagnostics.Debug.WriteLine($"[HannesView] Velocity_PointerPressed: tag={tag}");
        
        switch (tag)
        {
            case "WristPs_Neg":
                ViewModel.WristPsValue = -100;
                break;
            case "WristPs_Pos":
                ViewModel.WristPsValue = 100;
                break;
            case "Thumb_Neg":
                ViewModel.ThumbOpen();
                break;
            case "Thumb_Pos":
                ViewModel.ThumbClose();
                break;
        }
        
        // Visual feedback
        button.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8B5CF6"));
    }

    private void Velocity_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ReleaseVelocity(e.Source);
    }

    private void Velocity_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ReleaseVelocity(e.Source);
    }

    private void ReleaseVelocity(object? source)
    {
        var button = (source as Control)?.FindAncestorOfType<Button>();
        if (button?.Tag is null || ViewModel is null)
            return;

        var tag = button.Tag.ToString();
        
        // Only handle velocity buttons
        if (tag != "WristPs_Neg" && tag != "WristPs_Pos" && 
            tag != "Thumb_Neg" && tag != "Thumb_Pos")
            return;
            
        System.Diagnostics.Debug.WriteLine($"[HannesView] ReleaseVelocity: tag={tag}");
        
        switch (tag)
        {
            case "WristPs_Neg":
            case "WristPs_Pos":
                ViewModel.WristPsValue = 0;
                break;
            case "Thumb_Neg":
            case "Thumb_Pos":
                ViewModel.ThumbStop();
                break;
        }
        
        // Reset button color
        button.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A3A3A"));
    }

    #endregion
    
    // XAML event hooks are intentionally empty; velocity changes are handled by commands.
    private void VelocityButton_Pressed(object? sender, PointerPressedEventArgs e) { }
    private void VelocityButton_Released(object? sender, PointerReleasedEventArgs e) { }
    private void VelocityButton_CaptureLost(object? sender, PointerCaptureLostEventArgs e) { }
}
