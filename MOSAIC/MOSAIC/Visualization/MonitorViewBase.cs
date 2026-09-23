using Avalonia.Controls;
using Avalonia.Threading;
using MOSAIC.Services;

namespace MOSAIC.Visualization;

/// <summary>
/// Base class for monitor views. Owns the rendering lifecycle shared by every monitor —
/// visual-tree attach/detach, theme subscription, and the pause / reset / rebuild / resume
/// sequence — so a concrete view supplies only the backend-specific surface creation and
/// teardown. The monitor itself owns the data engine and is driven through <see cref="IMonitor"/>.
/// </summary>
/// <typeparam name="TMonitor">The monitor type this view renders.</typeparam>
public abstract class MonitorViewBase<TMonitor> : UserControl where TMonitor : class, IMonitor
{
    /// <summary>The monitor currently bound to this view, or <see langword="null"/>.</summary>
    protected TMonitor? Monitor { get; private set; }

    private bool _isAttached;
    private bool _themeSubscribed;

    /// <summary>Initialises the base and subscribes to theme changes.</summary>
    protected MonitorViewBase()
    {
        AttachedToVisualTree += (_, _) => OnAttached();
        DetachedFromVisualTree += (_, _) => OnDetached();
        SubscribeTheme();
    }

    /// <summary>
    /// Builds the rendering surface for the current <see cref="Monitor"/> and adds it to the
    /// view. Called only while the view is attached and a monitor is set.
    /// </summary>
    protected abstract void CreateSurface();

    /// <summary>Tears down the rendering surface. Must be safe to call when none exists.</summary>
    protected abstract void DestroySurface();

    /// <summary>
    /// Called after <see cref="Monitor"/> changes, before any rebuild. Override to wire
    /// per-view concerns such as a settings flyout or a mode toggle.
    /// </summary>
    protected virtual void OnMonitorAssigned(TMonitor? oldMonitor, TMonitor? newMonitor) { }

    /// <summary>Applies the active theme to view-owned chrome (e.g. container background).</summary>
    protected virtual void ApplyTheme() { }

    /// <summary>
    /// Sets the bound monitor, pausing the previous one and rebuilding the surface when the
    /// view is attached. Concrete views call this from their property-changed handler.
    /// </summary>
    protected void HandleMonitorChanged(TMonitor? next)
    {
        var old = Monitor;
        old?.Pause();
        Monitor = next;

        OnMonitorAssigned(old, next);

        if (Monitor != null && _isAttached)
            Rebuild();
    }

    private void OnAttached()
    {
        _isAttached = true;
        SubscribeTheme();
        if (Monitor != null) Rebuild();
    }

    private void OnDetached()
    {
        _isAttached = false;
        try
        {
            Monitor?.Pause();
            DestroySurface();
            UnsubscribeTheme();
        }
        catch { /* best-effort teardown */ }
    }

    /// <summary>
    /// The shared synchronous rebuild: pause, tear down, reset, build the surface, apply the
    /// theme, then resume. Runs on attach and whenever the monitor changes while attached.
    /// </summary>
    private void Rebuild()
    {
        Monitor!.Pause();
        DestroySurface();
        Monitor.ResetData();
        CreateSurface();
        ApplyTheme();
        Monitor.UpdateThemeColors();
        Monitor.Resume();
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplyTheme();
            Monitor?.UpdateThemeColors();
        }, DispatcherPriority.Render);
    }

    private void SubscribeTheme()
    {
        if (_themeSubscribed) return;
        App.ThemeChanged += OnThemeChanged;
        _themeSubscribed = true;
    }

    private void UnsubscribeTheme()
    {
        if (!_themeSubscribed) return;
        App.ThemeChanged -= OnThemeChanged;
        _themeSubscribed = false;
    }
}