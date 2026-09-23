using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MOSAIC.Components.Factory;
using MOSAIC.ViewModels.Palette;

namespace MOSAIC.Views.Palette;

public sealed partial class BlockPaletteView : UserControl
{
    /// <summary>
    /// Drag-drop format tag (carries the block's TypeKey) so a drop target can recognise a
    /// palette drag via <c>e.DataTransfer.Formats.Contains(BlockKeyFormat)</c>.
    /// </summary>
    public static readonly DataFormat<string> BlockKeyFormat =
        DataFormat.CreateStringApplicationFormat("mosaic-block-typekey");

    /// <summary>
    /// The descriptor currently being dragged from the palette. Set while a drag is in flight and
    /// read by the drop target (GraphCanvas) — the DataTransfer itself only carries the type key as
    /// a recognition tag. Single-pointer, so a single static slot is sufficient; cleared when the
    /// drag completes. Mirrors the field-based drag pattern used elsewhere in the app.
    /// </summary>
    public static BlockDescriptor? PendingDescriptor { get; private set; }

    /// <summary>Raised when the user clicks the palette's close (✕) button.</summary>
    public event EventHandler? CloseRequested;

    public BlockPaletteView()
    {
        InitializeComponent();
        DataContext = new BlockPaletteViewModel();
        BlocksScrollViewer.AddHandler(PointerPressedEvent, OnScrollViewerPointerPressed);
        UpdateBlockCount();
        ((BlockPaletteViewModel)DataContext).PropertyChanged += (_, _) => UpdateBlockCount();
    }

    private void UpdateBlockCount()
    {
        if (DataContext is BlockPaletteViewModel vm)
        {
            int total = 0;
            foreach (var g in vm.FilteredGroups) total += g.Blocks.Count;
            BlockCountText.Text = $"{total} blocks";
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnScrollViewerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // Walk up from the pressed element to find one whose DataContext is a BlockDescriptor.
        var element = e.Source as Visual;
        while (element != null)
        {
            if (element is Control { DataContext: BlockDescriptor descriptor })
            {
                _ = BeginDragAsync(e, descriptor);
                return;
            }
            element = element.GetVisualParent();
        }
    }

    private static async Task BeginDragAsync(PointerPressedEventArgs e, BlockDescriptor descriptor)
    {
        PendingDescriptor = descriptor;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(BlockKeyFormat, descriptor.TypeKey));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
        }
        finally
        {
            PendingDescriptor = null;
        }
    }
}
