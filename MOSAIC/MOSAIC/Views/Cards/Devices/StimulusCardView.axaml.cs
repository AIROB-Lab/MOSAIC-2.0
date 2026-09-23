using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using MOSAIC.ViewModels.Devices;

namespace MOSAIC.Views.Cards.Devices;

public partial class StimulusCardView : PopoutCardBase
{
    private static readonly DataFormat<string> TaskFormat =
        DataFormat.CreateStringApplicationFormat("mosaic-stimulus-task");

    private string? _draggedTask;

    public StimulusCardView()
    {
        InitializeComponent();
    }

    private async void OnTaskPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.DataContext is not string taskName) return;

        _draggedTask = taskName;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(TaskFormat, taskName));

        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    }

    private void OnTaskDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(TaskFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTaskDrop(object? sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        if (_draggedTask is null) return;
        if (this.DataContext is not StimulusViewModel vm) return;

        // Find which ListBoxItem the pointer is over by position
        var position = e.GetPosition(listBox);
        string? targetTask = null;

        foreach (var item in listBox.GetVisualDescendants().OfType<ListBoxItem>())
        {
            var bounds = item.Bounds;
            var itemPos = item.TranslatePoint(new Point(0, 0), listBox);
            if (itemPos is null) continue;

            var rect = new Rect(itemPos.Value, bounds.Size);
            if (rect.Contains(position))
            {
                targetTask = item.DataContext as string;
                break;
            }
        }

        if (targetTask is null || targetTask == _draggedTask)
        {
            _draggedTask = null;
            return;
        }

        var tasks = vm.TaskNames;
        var fromIndex = tasks.IndexOf(_draggedTask);
        var toIndex = tasks.IndexOf(targetTask);

        if (fromIndex >= 0 && toIndex >= 0)
            tasks.Move(fromIndex, toIndex);

        _draggedTask = null;
        e.Handled = true;
    }
}