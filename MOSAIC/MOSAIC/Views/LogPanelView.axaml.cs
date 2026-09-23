using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MOSAIC.Diagnostics;
using MOSAIC.ViewModels;

namespace MOSAIC.Views;

/// <summary>
/// The bottom log strip: a severity-filtered tail of the application log, with copy, clear and a
/// shortcut to the log folder.
/// </summary>
/// <remarks>
/// The tick subscription is tied to the visual tree the same way the device cards do it, with one
/// addition: a hidden control is still attached, and MainView keeps this one hidden until the header
/// button is pressed, so visibility is part of the condition. Otherwise the panel would pull from the
/// ring for the whole session while never being looked at.
/// </remarks>
public partial class LogPanelView : UserControl
{
    private bool _attached;
    private bool _scrollPending;

    /// <summary>Raised when the user clicks the panel's close (✕) button.</summary>
    public event EventHandler? CloseRequested;

    public LogPanelView()
    {
        InitializeComponent();
        DataContext = new LogPanelViewModel();
    }

    private LogPanelViewModel? ViewModel => DataContext as LogPanelViewModel;

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateTicking();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        UpdateTicking();
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
            UpdateTicking();
    }

    private void UpdateTicking()
    {
        if (ViewModel is not { } vm) return;

        if (_attached && IsVisible)
        {
            vm.Entries.CollectionChanged -= OnEntriesChanged;
            vm.Entries.CollectionChanged += OnEntriesChanged;
            vm.Start();
        }
        else
        {
            vm.Entries.CollectionChanged -= OnEntriesChanged;
            vm.Stop();
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel is not { AutoScroll: true }) return;

        // One scroll per tick, not one per notification: a tick appends and trims, and changing the
        // filter rebuilds, so without this gate a burst would queue a job per change for a scroll
        // that only ever has one destination. The flag is UI-thread-only, like the notification.
        if (_scrollPending) return;
        _scrollPending = true;

        // Posted at Background priority so the scroll runs after the new rows have been laid out;
        // scrolling now would land on the extent the panel had before they arrived.
        Dispatcher.UIThread.Post(ScrollToNewest, DispatcherPriority.Background);
    }

    private void ScrollToNewest()
    {
        _scrollPending = false;
        if (ViewModel is not { AutoScroll: true }) return;

        var last = LogList.ItemCount - 1;
        if (last >= 0) LogList.ScrollIntoView(last);
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        // Another process can hold the clipboard, and this is async void: an escaping exception
        // would take down the one panel whose job is to still be there when things go wrong.
        try
        {
            await clipboard.SetTextAsync(vm.BuildClipboardText());
        }
        catch (Exception ex)
        {
            Log.Warn("LogPanel", ex, "Could not copy the log to the clipboard.");
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);
}
