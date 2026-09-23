using Avalonia.Controls;
using Avalonia.Interactivity;
using MOSAIC.Components.Basics;
using MOSAIC.ViewModels.Dialogs;

namespace MOSAIC.Views.Dialogs;

public partial class AddBlockDialog : Window
{
    /// <summary>The built config when the user confirmed; null if cancelled or validation failed.</summary>
    public JsonModel? Result { get; private set; }

    /// <summary>
    /// Whether the user asked for the new block to start out recording.
    /// </summary>
    /// <remarks>
    /// Carried separately from <see cref="Result"/> because the pipeline JSON has no recording field.
    /// The caller applies it to the block once the factory has built it.
    /// </remarks>
    public bool StartRecording { get; private set; }

    public AddBlockDialog()
    {
        InitializeComponent();
    }

    private void OnAddClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddBlockDialogViewModel vm)
            return;

        var model = vm.TryBuildModel();
        if (model is null)
            return;

        Result = model;
        StartRecording = vm.Record;
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
