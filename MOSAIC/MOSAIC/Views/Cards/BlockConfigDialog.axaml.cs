using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MOSAIC.Views.Cards;

/// <summary>
/// Reusable modal dialog that hosts a block's configuration editor (a per-card content view) in a
/// roomy, clearly-detached window instead of a cramped in-card flyout. The card opens it with its
/// own ViewModel as <see cref="Avalonia.StyledElement.DataContext"/>, which the injected body inherits, so the
/// existing bindings keep working unchanged.
/// </summary>
public partial class BlockConfigDialog : Window
{
    public BlockConfigDialog() => InitializeComponent();

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Opens the config <paramref name="body"/> modally over <paramref name="anchor"/>'s window,
    /// bound to <paramref name="dataContext"/> (the card's ViewModel).</summary>
    public static void Show(Control anchor, object? dataContext, Control body)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner) return;

        var dialog = new BlockConfigDialog { DataContext = dataContext };
        dialog.Body.Content = body;
        dialog.ShowDialog(owner);
    }
}
