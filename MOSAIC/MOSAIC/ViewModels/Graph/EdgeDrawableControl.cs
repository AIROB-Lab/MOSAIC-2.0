using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using MOSAIC;

namespace MOSAIC.ViewModels.Graph;

/// <summary>State of the in-flight wire while the user drags a connection.</summary>
public enum PendingKind { None, Neutral, Valid, Invalid }

public sealed class EdgeDrawableControl : Control
{
    // In-flight connection wire (build-mode drag). Drawn on top of the committed edges.
    private static readonly Pen PendGreen = new(new SolidColorBrush(Color.Parse("#1D9E75")), 2.5) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
    private static readonly Pen PendRed = new(new SolidColorBrush(Color.Parse("#E24B4A")), 2.5) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
    private static readonly Pen PendGray = new(new SolidColorBrush(Color.Parse("#9CA3AF")), 2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };

    private Point _pendFrom, _pendTo;
    private PendingKind _pend = PendingKind.None;

    /// <summary>Show the in-flight wire from <paramref name="from"/> to <paramref name="to"/> in the given state.</summary>
    public void SetPending(Point from, Point to, PendingKind kind)
    {
        _pendFrom = from;
        _pendTo = to;
        _pend = kind;
        InvalidateVisual();
    }

    /// <summary>Hide the in-flight wire.</summary>
    public void ClearPending()
    {
        _pend = PendingKind.None;
        InvalidateVisual();
    }

    public static readonly StyledProperty<ObservableCollection<VertexViewModel>?> VerticesProperty =
        AvaloniaProperty.Register<EdgeDrawableControl, ObservableCollection<VertexViewModel>?>(nameof(Vertices));

    public ObservableCollection<VertexViewModel>? Vertices
    {
        get => GetValue(VerticesProperty);
        set => SetValue(VerticesProperty, value);
    }

    readonly Dictionary<VertexViewModel, (PropertyChangedEventHandler vm, PropertyChangedEventHandler? val)>
        _subs = new();

    // Theme-aware colors
    private Pen _strokePen = null!;
    private IBrush _fillBrush = null!;
    private IBrush _textBrush = null!;

    public EdgeDrawableControl()
    {
        UpdateBrushes();
        App.ThemeChanged += OnThemeChanged;
        
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) InvalidateVisual();
            else if (e.Property == VerticesProperty)
            {
                ResetSubs();
                InvalidateVisual();
            }
        };
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateBrushes();
            InvalidateVisual();
        });
    }

    private void UpdateBrushes()
    {
        var isDark = App.CurrentTheme == AppTheme.Dark;

        if (isDark)
        {
            // Dark theme - light gray lines on dark background
            _strokePen = new Pen(new SolidColorBrush(Color.Parse("#9CA3AF")), 2);
            _fillBrush = new SolidColorBrush(Color.Parse("#1F2937"));
            _textBrush = new SolidColorBrush(Color.Parse("#E5E7EB"));
        }
        else
        {
            // Light theme - dark gray lines on light background
            _strokePen = new Pen(new SolidColorBrush(Color.Parse("#4B5563")), 2);
            _fillBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
            _textBrush = new SolidColorBrush(Color.Parse("#1F2937"));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        App.ThemeChanged -= OnThemeChanged;
        UnsubAll();
    }

    void ResetSubs()
    {
        UnsubAll();
        if (Vertices is null) return;
        Vertices.CollectionChanged += OnChanged;
        foreach (var v in Vertices) Sub(v);
    }

    void OnChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ResetSubs();
            InvalidateVisual();
            return;
        }

        if (e.OldItems != null)
            foreach (VertexViewModel v in e.OldItems)
                Unsub(v);
        if (e.NewItems != null)
            foreach (VertexViewModel v in e.NewItems)
                Sub(v);
        InvalidateVisual();
    }

    void Sub(VertexViewModel vm)
    {
        PropertyChangedEventHandler vmH = (_, a) =>
        {
            if (a.PropertyName is nameof(VertexViewModel.X) or nameof(VertexViewModel.Y) or "Width" or "Height")
                InvalidateVisual();
        };
        vm.PropertyChanged += vmH;
        PropertyChangedEventHandler? valH = null;
        if (vm.Value is INotifyPropertyChanged npc)
        {
            valH = (_, a) =>
            {
                if (a.PropertyName == "FrequencyText") InvalidateVisual();
            };
            npc.PropertyChanged += valH;
        }

        _subs[vm] = (vmH, valH);
    }

    void Unsub(VertexViewModel vm)
    {
        if (_subs.TryGetValue(vm, out var p))
        {
            vm.PropertyChanged -= p.vm;
            if (vm.Value is INotifyPropertyChanged npc && p.val != null) npc.PropertyChanged -= p.val;
            _subs.Remove(vm);
        }
    }

    void UnsubAll()
    {
        if (Vertices != null) Vertices.CollectionChanged -= OnChanged;
        foreach (var kv in _subs)
        {
            var vm = kv.Key;
            var vmH = kv.Value.vm;
            var valH = kv.Value.val;
            vm.PropertyChanged -= vmH;
            if (vm.Value is INotifyPropertyChanged npc && valH != null) npc.PropertyChanged -= valH;
        }

        _subs.Clear();
    }


    public override void Render(DrawingContext c)
    {
        base.Render(c);
        if (Vertices is null) return;

        // First pass: draw all arrows (behind). An edge runs from the producer `to`'s output port
        // to the consumer `from`'s i-th input port (from.Neighbors are the block's inputs, in order).
        foreach (var from in Vertices)
        {
            int portCount = from.InputPortCount;
            var inputs = from.Neighbors;
            for (int i = 0; i < inputs.Count; i++)
            {
                var start = PortLayout.OutputAnchor(inputs[i]);
                var end = InputAnchorFor(from, i, portCount);
                DrawArrow(c, start, end, _strokePen);
            }
        }

        // Second pass: draw all rate boxes (in front)
        foreach (var from in Vertices)
        {
            int portCount = from.InputPortCount;
            var inputs = from.Neighbors;
            for (int i = 0; i < inputs.Count; i++)
            {
                var to = inputs[i];
                var start = PortLayout.OutputAnchor(to);
                var end = InputAnchorFor(from, i, portCount);
                var mid = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);

                // Rate label box
                var rect = new Rect(mid.X - 32.5, mid.Y - 13, 65, 26);
                c.DrawRectangle(_fillBrush, _strokePen, rect, 4, 4);

                // The rate on an A→B arrow is the SOURCE block A's output rate. Here `to` is the
                // parent (from.Neighbors are a block's inputs), i.e. the producer feeding `from` —
                // so label with `to`, not the consumer `from`. (A two-input block was wrongly
                // showing its own rate on both incoming arrows.)
                var text = to.Value.FrequencyText ?? string.Empty;
                var tl = new TextLayout(text, Typeface.Default, 12, _textBrush, TextAlignment.Center,
                    TextWrapping.NoWrap, TextTrimming.None, textDecorations: null,
                    flowDirection: FlowDirection.LeftToRight, maxWidth: rect.Width);
                tl.Draw(c, new Point(rect.X, rect.Y + (rect.Height - tl.Height) / 2));
            }
        }

        // In-flight connection wire (on top of everything).
        if (_pend != PendingKind.None)
        {
            var pen = _pend switch
            {
                PendingKind.Valid   => PendGreen,
                PendingKind.Invalid => PendRed,
                _                   => PendGray
            };
            c.DrawLine(pen, _pendFrom, _pendTo);
            c.DrawEllipse(null, pen, _pendTo, 7, 7);
        }
    }

    // Anchor for the consumer's i-th input; falls back to the node centre if it has no input ports.
    static Point InputAnchorFor(VertexViewModel consumer, int index, int portCount) =>
        portCount > 0
            ? PortLayout.InputAnchor(consumer, Math.Min(index, portCount - 1), portCount)
            : PortLayout.Center(consumer);

    static void DrawArrow(DrawingContext ctx, Point start, Point end, Pen pen)
    {
        ctx.DrawLine(pen, start, end);
        const double s = 15, a = Math.PI / 7;
        var th = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var h1 = new Point(end.X - s * Math.Cos(th - a), end.Y - s * Math.Sin(th - a));
        var h2 = new Point(end.X - s * Math.Cos(th + a), end.Y - s * Math.Sin(th + a));
        ctx.DrawLine(pen, end, h1);
        ctx.DrawLine(pen, end, h2);
    }
}