using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;

namespace MOSAIC.Services;

/// <summary>
/// Pops any UserControl out into a standalone window.
/// Only available on Desktop (Windows/macOS/Linux).
///
/// Usage from a ViewModel command or code-behind:
/// <code>
///   PopoutHelper.TryPopout(myVisualizationPanel, "EMG Monitor", 800, 500);
/// </code>
///
/// The control is removed from its current parent, placed in the new window,
/// and returned to its original parent when the window closes.
/// </summary>
public static class PopoutHelper
{
    /// <summary>
    /// Returns true only when the running Avalonia host supports desktop windows.
    /// Single-view mobile/browser hosts must never construct a Window, even when
    /// shared code was compiled on a desktop OS.
    /// </summary>
    public static bool IsDesktop => SupportsDesktopWindows(Application.Current?.ApplicationLifetime);

    internal static bool SupportsDesktopWindows(IApplicationLifetime? lifetime, bool mobileLayout = false)
        => !mobileLayout && lifetime is IClassicDesktopStyleApplicationLifetime;

    /// <summary>
    /// Detaches <paramref name="control"/> from its current parent and opens it
    /// in a new window. When the window closes, the control is re-attached to
    /// its original parent.
    /// </summary>
    /// <param name="control">The control to pop out.</param>
    /// <param name="title">Window title.</param>
    /// <param name="width">Initial window width.</param>
    /// <param name="height">Initial window height.</param>
    /// <returns>True if the window was opened, false if not on desktop or control has no parent.</returns>
    public static bool TryPopout(Control control, string title = "MOSAIC", int width = 900, int height = 600)
    {
        if (!IsDesktop) return false;
        if (control.Parent is not ContentControl parentContent &&
            control.Parent is not Panel parentPanel)
            return false;

        // Remember original parent and position
        var originalParent = control.Parent;
        int originalIndex = -1;

        // Detach from current parent
        if (originalParent is ContentControl cc)
        {
            cc.Content = null;
        }
        else if (originalParent is Panel panel)
        {
            originalIndex = panel.Children.IndexOf(control);
            panel.Children.Remove(control);
        }

        // Create the popout window
        var window = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = 400,
            MinHeight = 300,
            Content = control,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        // When window closes, return control to original parent
        window.Closed += (_, _) =>
        {
            window.Content = null;

            if (originalParent is ContentControl cc2)
            {
                cc2.Content = control;
            }
            else if (originalParent is Panel panel2)
            {
                if (originalIndex >= 0 && originalIndex <= panel2.Children.Count)
                    panel2.Children.Insert(originalIndex, control);
                else
                    panel2.Children.Add(control);
            }
        };

        window.Show();
        return true;
    }

    /// <summary>
    /// Clones the DataContext and opens a fresh instance of
    /// <typeparamref name="T"/> in a new window. The original control
    /// stays in place — useful when you don't want to detach it.
    /// </summary>
    public static bool TryPopoutCopy<T>(object? dataContext, string title = "MOSAIC",
                                         int width = 900, int height = 600)
        where T : Control, new()
    {
        if (!IsDesktop) return false;

        var control = new T { DataContext = dataContext };

        var window = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = 400,
            MinHeight = 300,
            Content = control,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        window.Show();
        return true;
    }
}
