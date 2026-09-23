using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MOSAIC.Visualization.Scatter3D;

/// <summary>
/// Code-only Avalonia view for <see cref="Scatter3DMonitor"/>.
/// Handles mouse drag to rotate the 3D view (azimuth/elevation).
/// Starts the monitor only while this view is really on screen, and leaves it running when
/// another attached view - a pop-out card cloned from the same DataContext - still shows it.
/// </summary>
public class Scatter3DView : UserControl
{
    public static readonly StyledProperty<object?> ScatterProperty =
        AvaloniaProperty.Register<Scatter3DView, object?>(nameof(Scatter));

    public object? Scatter
    {
        get => GetValue(ScatterProperty);
        set => SetValue(ScatterProperty, value);
    }

    private Image? _image;
    private Scatter3DMonitor? _currentMonitor;
    private bool _isAttached;
    private int _generation;
    private readonly object _bindLock = new();

    /// <summary>
    /// Every <see cref="Scatter3DView"/> currently attached to a visual tree, so a detaching
    /// view can tell whether the monitor it is dropping is still on screen somewhere else.
    /// </summary>
    private static readonly List<Scatter3DView> AttachedViews = new();

    private static readonly object AttachedViewsLock = new();

    // Mouse drag state
    private bool _isDragging;
    private Point _lastDragPoint;

    static Scatter3DView()
    {
        ScatterProperty.Changed.AddClassHandler<Scatter3DView>((view, _) => view.OnMonitorChanged());
    }

    public Scatter3DView()
    {
        BuildLayout();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private Border? _container;

    private void BuildLayout()
    {
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);

        _container = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
            Child = _image,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        // Mouse events for camera rotation + zoom
        _container.PointerPressed += OnPointerPressed;
        _container.PointerMoved += OnPointerMoved;
        _container.PointerReleased += OnPointerReleased;
        _container.PointerCaptureLost += (_, _) => _isDragging = false;
        _container.PointerWheelChanged += OnPointerWheel;

        Content = new SquareViewport { Child = _container };
    }

    // Reserve the square during measurement, before the parent positions the next section.
    // A vertical ScrollViewer supplies infinite height; the available width then sets both sides.
    private sealed class SquareViewport : Decorator
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            var side = Math.Min(availableSize.Width, availableSize.Height);
            if (double.IsInfinity(side)) side = 320;
            var size = new Size(side, side);
            Child?.Measure(size);
            return size;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var side = Math.Min(finalSize.Width, finalSize.Height);
            Child?.Arrange(new Rect((finalSize.Width - side) / 2,
                (finalSize.Height - side) / 2, side, side));
            return finalSize;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Monitor changed
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rebinds to the new <see cref="Scatter"/> monitor, releasing the outgoing one unless another
    /// attached view is still showing it.
    /// </summary>
    private void OnMonitorChanged()
    {
        lock (_bindLock)
        {
            if (_currentMonitor != null)
            {
                _currentMonitor.PropertyChanged -= OnMonitorPropertyChanged;

                // A popped-out card gives two attached views the same monitor, so a rebind here
                // (DataContext reset, container recycling) must not stop the other view's frames -
                // nothing would resume them, since that view never detaches.
                if (!IsMonitorShownElsewhere(_currentMonitor)) _currentMonitor.Pause();

                _image?.ClearValue(Image.SourceProperty);
            }

            _currentMonitor = Scatter as Scatter3DMonitor;

            if (_currentMonitor != null && _isAttached)
            {
                BindToMonitor();
                if (ShouldRender) _currentMonitor.Resume();
            }
        }
    }

    private void BindToMonitor()
    {
        _image?.Bind(Image.SourceProperty,
            new Binding(nameof(Scatter3DMonitor.Image))
            {
                Source = _currentMonitor,
                Mode = BindingMode.OneWay,
            });

        // The monitor renders into reused WriteableBitmaps; on some compositor builds a Source ref
        // swap alone doesn't force a re-composite, so invalidate the Image on each published frame.
        if (_currentMonitor != null)
        {
            _currentMonitor.PropertyChanged -= OnMonitorPropertyChanged; // guard against double-subscribe
            _currentMonitor.PropertyChanged += OnMonitorPropertyChanged;
        }
    }

    private void OnMonitorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Scatter3DMonitor.Image))
            _image?.InvalidateVisual();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see langword="true"/> when this view is attached and no ancestor hides it, i.e. when the
    /// monitor's frames actually reach the screen. <see cref="Visual.IsEffectivelyVisible"/> is
    /// only maintained while a control is in a visual tree, hence the <c>_isAttached</c> term.
    /// </summary>
    private bool ShouldRender => _isAttached && IsEffectivelyVisible;

    /// <summary>
    /// <see langword="true"/> when an attached view other than this one is bound to
    /// <paramref name="monitor"/>.
    /// </summary>
    private bool IsMonitorShownElsewhere(Scatter3DMonitor monitor)
    {
        lock (AttachedViewsLock)
        {
            foreach (var view in AttachedViews)
            {
                if (!ReferenceEquals(view, this) && ReferenceEquals(view._currentMonitor, monitor))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Binds to the monitor and starts it, but only if this view is really on screen.</summary>
    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = true;

        lock (AttachedViewsLock)
        {
            if (!AttachedViews.Contains(this)) AttachedViews.Add(this);
        }

        var gen = Interlocked.Increment(ref _generation);

        Dispatcher.UIThread.Post(() =>
        {
            // Stale callback — a newer detach/attach has happened
            if (gen != Volatile.Read(ref _generation)) return;

            lock (_bindLock)
            {
                if (_currentMonitor == null)
                    _currentMonitor = Scatter as Scatter3DMonitor;

                if (_currentMonitor != null)
                {
                    BindToMonitor();

                    // Resuming unconditionally would render 800x800 at 30 fps behind a collapsed
                    // pane: a card popped out with "3D" unticked attaches inside a hidden Border.
                    if (ShouldRender) _currentMonitor.Resume();
                }
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Unbinds from the monitor and stops it, unless another attached view is still showing it.
    /// </summary>
    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;

        lock (AttachedViewsLock)
        {
            AttachedViews.Remove(this);
        }

        var gen = Interlocked.Increment(ref _generation);

        if (_currentMonitor != null)
        {
            _currentMonitor.PropertyChanged -= OnMonitorPropertyChanged;

            // A closing pop-out shares its monitor with the original card, which was only hidden
            // and so never gets an attach event to restart it. Hand the monitor over instead.
            if (!IsMonitorShownElsewhere(_currentMonitor)) _currentMonitor.Pause();
        }

        Dispatcher.UIThread.Post(() =>
        {
            // Only clear if no newer attach has happened since
            if (gen == Volatile.Read(ref _generation) && !_isAttached)
            {
                _image?.ClearValue(Image.SourceProperty);
            }
        }, DispatcherPriority.Background);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Mouse drag → camera orbit
    // ═══════════════════════════════════════════════════════════════════

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && _currentMonitor != null)
        {
            _isDragging = true;
            _lastDragPoint = e.GetPosition(this);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || _currentMonitor == null) return;

        var pos = e.GetPosition(this);
        double dx = pos.X - _lastDragPoint.X;
        double dy = pos.Y - _lastDragPoint.Y;
        _lastDragPoint = pos;

        // Horizontal drag → azimuth, vertical drag → elevation
        const double sensitivity = 0.5;
        _currentMonitor.Azimuth += dx * sensitivity;
        _currentMonitor.Elevation = Math.Clamp(_currentMonitor.Elevation - dy * sensitivity, -89, 89);

        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_currentMonitor == null) return;

        double delta = e.Delta.Y * 0.15;
        _currentMonitor.Zoom = Math.Clamp(_currentMonitor.Zoom + delta, 0.2, 10.0);
        e.Handled = true;
    }
}
