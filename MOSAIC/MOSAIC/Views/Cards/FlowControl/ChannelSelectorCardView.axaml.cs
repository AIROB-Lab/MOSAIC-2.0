using Avalonia.Controls;
using Avalonia.Input;
using MOSAIC.ViewModels.FlowControl;

namespace MOSAIC.Views.Cards.FlowControl;

public partial class ChannelSelectorCardView : PopoutCardBase
{
    public ChannelSelectorCardView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Handles clicking on a channel pill to toggle its selection.
    /// </summary>
    private void OnChannelClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is ChannelState channel)
        {
            channel.IsActive = !channel.IsActive;
            e.Handled = true;
        }
    }
}