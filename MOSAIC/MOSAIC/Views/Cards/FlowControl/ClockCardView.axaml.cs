using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MOSAIC.Views.Cards;

public partial class ClockCardView : PopoutCardBase
{
    public ClockCardView()
    {
        InitializeComponent();

        RateTextBox.AddHandler(TextInputEvent, OnRateTextInput, RoutingStrategies.Tunnel);
        RateTextBox.AddHandler(KeyDownEvent, OnRateKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnRateTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text != null && !e.Text.All(char.IsDigit))
        {
            e.Handled = true;
        }
    }

    private void OnRateKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Back or Key.Delete)
        {
            var text = RateTextBox.Text ?? "";
            var selectedLength = RateTextBox.SelectedText?.Length ?? 0;

            // Set to 0 if deletion would leave empty field
            if (text.Length <= 1 || selectedLength >= text.Length)
            {
                RateTextBox.Text = "0";
                RateTextBox.SelectAll();
                e.Handled = true;
            }
        }
    }
}