using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Services;
using MOSAIC.Visualization;
using MOSAIC.Visualization.ScopeMonitor;
using ScottPlot;
using ScottPlot.Plottables;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Visualization.SnapshotMonitor;

/// <summary>
/// Real-time A-mode snapshot monitor backed by ScottPlot.
///
/// <para><b>Architecture:</b> Unlike <see cref="ScopeMonitor.ScopeMonitor"/> which streams
/// multi-channel time-series data, this monitor displays spatial waveforms
/// (e.g., ultrasound A-mode lines) as snapshots — X axis is sample index (depth),
/// Y axis is amplitude. The entire waveform is replaced on each frame.</para>
///
/// <para><b>Multi-trace:</b> When fed a <see cref="Matrix"/>, each row becomes a
/// separate colored trace (one per TX/RX config). Colors reuse
/// <see cref="ScopeMonitor.ScopeMonitor.ChannelColors"/> for consistency.
/// When fed a <see cref="Vector"/>, a single trace is shown.</para>
/// </summary>
public partial class SnapshotMonitor : ObservableObject, IDisposable
{
    #region Constants

    private const int MaxTraces = 16;

    #endregion

    #region Fields

    private readonly Action _timerCallback;
    private readonly ConcurrentQueue<SnapshotFrame> _queue = new();

    private volatile bool _disposed;
    private volatile bool _isActive;
    private volatile bool _isUpdating;

    private int _flushScheduled;

    /// <summary>ScottPlot Plot object — set by the view via <see cref="AttachPlot"/>.</summary>
    private Plot? _plot;

    /// <summary>Callback to invoke AvaPlot.Refresh() on the view.</summary>
    private Action? _refreshCallback;

    /// <summary>One Signal plottable per trace (config row).</summary>
    private readonly List<Signal> _signals = new();

    /// <summary>Backing data arrays for each Signal plottable.</summary>
    private readonly List<double[]> _traceData = new();

    /// <summary>Per-trace visibility state.</summary>
    private bool[] _traceVisible = Array.Empty<bool>();

    /// <summary>Current number of traces (rows) being displayed.</summary>
    private int _traceCount;

    /// <summary>Current number of samples per trace.</summary>
    private int _sampleCount;

    #endregion

    #region Observable Properties

    /// <summary>Whether the Y axis auto-scales to the data range each frame.</summary>
    [ObservableProperty] private bool _autoScale = true;

    /// <summary>Total number of frames rendered.</summary>
    [ObservableProperty] private long _frameCount;

    #endregion

    #region Configuration Properties

    private VisualizationTimer.TickRate _tickRate = VisualizationTimer.TickRate.Fps30;

    /// <summary>Timer tick rate for flush cycles.</summary>
    public VisualizationTimer.TickRate TickRate
    {
        get => _tickRate;
        set
        {
            if (_tickRate == value) return;
            var wasActive = _isActive;
            if (wasActive) Pause();
            _tickRate = value;
            if (wasActive) Resume();
        }
    }

    /// <summary>Line thickness in pixels.</summary>
    public float LineWidth { get; set; } = 2.0f;

    #endregion

    #region Trace Visibility

    /// <summary>Current number of traces (config rows). Updated when matrix dimensions change.</summary>
    public int TraceCount => _traceCount;

    /// <summary>
    /// Fired when the trace count changes (new matrix dimensions) so the view can
    /// rebuild its toggle buttons. Raised on the thread that calls FlushToChart (UI thread).
    /// </summary>
    public event Action? TracesChanged;

    /// <summary>Returns whether the trace at <paramref name="index"/> is currently visible.</summary>
    public bool IsTraceVisible(int index)
        => index >= 0 && index < _traceVisible.Length && _traceVisible[index];

    /// <summary>Toggles visibility of a single trace and updates the Signal plottable.</summary>
    public void SetTraceVisible(int index, bool visible)
    {
        if (index < 0 || index >= _traceVisible.Length) return;
        _traceVisible[index] = visible;
        if (index < _signals.Count)
            _signals[index].IsVisible = visible;
        _refreshCallback?.Invoke();
    }

    /// <summary>Shows all traces.</summary>
    public void ShowAllTraces()
    {
        for (int i = 0; i < _traceVisible.Length; i++)
        {
            _traceVisible[i] = true;
            if (i < _signals.Count)
                _signals[i].IsVisible = true;
        }
        _refreshCallback?.Invoke();
    }

    /// <summary>Shows only the trace at <paramref name="index"/>, hides all others.</summary>
    public void ShowSingleTrace(int index)
    {
        for (int i = 0; i < _traceVisible.Length; i++)
        {
            _traceVisible[i] = i == index;
            if (i < _signals.Count)
                _signals[i].IsVisible = i == index;
        }
        _refreshCallback?.Invoke();
    }

    #endregion

    #region Internal Frame Type

    /// <summary>
    /// Snapshot payload: either a single row or multiple rows.
    /// </summary>
    private readonly struct SnapshotFrame
    {
        public readonly double[][] Rows;
        public readonly int SampleCount;

        public SnapshotFrame(double[] singleRow)
        {
            Rows = new[] { singleRow };
            SampleCount = singleRow.Length;
        }

        public SnapshotFrame(double[][] rows, int sampleCount)
        {
            Rows = rows;
            SampleCount = sampleCount;
        }
    }

    #endregion

    #region Lifecycle

    public SnapshotMonitor()
    {
        _timerCallback = RequestFlush;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Pause();
        _plot = null;
        _refreshCallback = null;
        _signals.Clear();
        _traceData.Clear();
        while (_queue.TryDequeue(out _)) { }
    }

    #endregion

    #region Plot Attachment (called by SnapshotMonitorView)

    /// <summary>
    /// Wires this monitor to a ScottPlot <see cref="Plot"/> and a refresh callback.
    /// </summary>
    public void AttachPlot(Plot plot, Action refreshCallback)
    {
        _plot = plot;
        _refreshCallback = refreshCallback;
        ConfigurePlotAppearance();
        RebuildSignals();
    }

    /// <summary>
    /// Detaches from the plot.
    /// </summary>
    public void DetachPlot()
    {
        _plot = null;
        _refreshCallback = null;
        _signals.Clear();
        _traceData.Clear();
    }

    #endregion

    #region Data Input

    /// <summary>
    /// Enqueues a vector snapshot (single trace). Thread-safe.
    /// </summary>
    public void EnqueueSnapshot(Vector data)
    {
        if (_disposed || data is null || data.Count == 0) return;
        if (_isUpdating || !_isActive) return;

        _queue.Enqueue(new SnapshotFrame(data.ToArray()));
    }

    /// <summary>
    /// Enqueues a matrix snapshot (one trace per row). Thread-safe.
    /// </summary>
    public void EnqueueSnapshot(Matrix data)
    {
        if (_disposed || data is null || data.RowCount == 0) return;
        if (_isUpdating || !_isActive) return;

        int rows = Math.Min(data.RowCount, MaxTraces);
        int cols = data.ColumnCount;
        var rowArrays = new double[rows][];
        for (int r = 0; r < rows; r++)
            rowArrays[r] = data.Row(r).ToArray();

        _queue.Enqueue(new SnapshotFrame(rowArrays, cols));
    }

    #endregion

    #region Pause / Resume

    public void Pause()
    {
        _isActive = false;
        VisualizationTimer.Instance.Unsubscribe(_timerCallback);
        while (_queue.TryDequeue(out _)) { }
    }

    public void Resume()
    {
        if (_disposed) return;
        while (_queue.TryDequeue(out _)) { }
        _isActive = true;
        VisualizationTimer.Instance.Subscribe(_timerCallback, _tickRate);
    }

    #endregion

    #region Reset

    public void ResetData()
    {
        if (_disposed) return;

        if (Dispatcher.UIThread.CheckAccess())
            ResetDataCore();
        else
            Dispatcher.UIThread.Invoke(ResetDataCore);
    }

    private void ResetDataCore()
    {
        while (_queue.TryDequeue(out _)) { }
        RebuildSignals();
    }

    #endregion

    #region Timer Flush

    private void RequestFlush()
    {
        if (_disposed || !_isActive || _isUpdating) return;
        if (_queue.IsEmpty) return;

        if (Interlocked.Exchange(ref _flushScheduled, 1) == 1) return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            FlushToChart();
        }, DispatcherPriority.Render);
    }

    private void FlushToChart()
    {
        if (_disposed || !_isActive || _isUpdating || _plot == null) return;

        _isUpdating = true;
        try
        {
            // Drain queue, keep only last frame
            SnapshotFrame? last = null;
            while (_queue.TryDequeue(out var frame))
                last = frame;

            if (last is null) return;

            var snapshot = last.Value;

            // Rebuild signals if trace count or sample count changed
            if (snapshot.Rows.Length != _traceCount || snapshot.SampleCount != _sampleCount)
            {
                _traceCount = snapshot.Rows.Length;
                _sampleCount = snapshot.SampleCount;
                RebuildSignals();
            }

            // Copy data into each signal's backing array
            for (int t = 0; t < _traceCount && t < _traceData.Count; t++)
            {
                var src = snapshot.Rows[t];
                var dst = _traceData[t];
                int len = Math.Min(src.Length, dst.Length);
                Array.Copy(src, dst, len);

                // Sanitize NaN/Inf
                for (int i = 0; i < len; i++)
                {
                    if (double.IsNaN(dst[i]) || double.IsInfinity(dst[i]))
                        dst[i] = 0.0;
                }
            }

            UpdateAxisLimits();
            FrameCount++;
            _refreshCallback?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SnapshotMonitor] FlushToChart error: {ex.Message}");
        }
        finally
        {
            _isUpdating = false;
        }
    }

    #endregion

    #region Signal Management

    private void RebuildSignals()
    {
        if (_plot == null) return;

        // Remove old signals
        foreach (var s in _signals)
            _plot.Remove(s);
        _signals.Clear();
        _traceData.Clear();

        if (_sampleCount <= 0 || _traceCount <= 0)
        {
            _traceVisible = Array.Empty<bool>();
            _refreshCallback?.Invoke();
            TracesChanged?.Invoke();
            return;
        }

        // Preserve visibility state across rebuilds if trace count unchanged
        if (_traceVisible.Length != _traceCount)
        {
            _traceVisible = new bool[_traceCount];
            Array.Fill(_traceVisible, true);
        }

        float thickness = _traceCount > 8 ? 1.0f : _traceCount > 4 ? 1.5f : LineWidth;

        for (int t = 0; t < _traceCount; t++)
        {
            var data = new double[_sampleCount];
            _traceData.Add(data);

            var signal = _plot.Add.Signal(data);
            var skColor = ScopeMonitor.ScopeMonitor.GetChannelColor(t);
            signal.Color = new ScottPlot.Color(skColor.Red, skColor.Green, skColor.Blue);
            signal.LineWidth = thickness;
            signal.IsVisible = _traceVisible[t];
            _signals.Add(signal);
        }

        _refreshCallback?.Invoke();
        TracesChanged?.Invoke();
    }

    #endregion

    #region Axis Limits

    private void UpdateAxisLimits()
    {
        if (_plot == null || _sampleCount == 0) return;

        // X axis: always 0 to sampleCount
        _plot.Axes.SetLimitsX(0, _sampleCount);

        if (!AutoScale) return;

        // Y axis: auto-scale across visible traces
        double min = double.MaxValue;
        double max = double.MinValue;
        for (int t = 0; t < _traceData.Count; t++)
        {
            if (!_traceVisible[t]) continue;
            var data = _traceData[t];
            for (int i = 0; i < data.Length; i++)
            {
                var v = data[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        if (min >= max)
        {
            _plot.Axes.SetLimitsY(-1, 1);
            return;
        }

        double range = max - min;
        double padding = range * 0.1;
        _plot.Axes.SetLimitsY(min - padding, max + padding);
    }

    /// <summary>
    /// Sets a fixed Y-axis range. Disables auto-scaling.
    /// </summary>
    public void SetFixedRange(double yMin, double yMax)
    {
        if (yMin >= yMax) return;
        AutoScale = false;
        _plot?.Axes.SetLimitsY(yMin, yMax);
    }

    /// <summary>
    /// Re-enables auto-scaling.
    /// </summary>
    public void SetAutoScale()
    {
        AutoScale = true;
    }

    #endregion

    #region Plot Appearance

    public void ConfigurePlotAppearance()
    {
        if (_plot == null) return;

        var isDark = App.CurrentTheme == AppTheme.Dark;

        // Background
        var bgColor = isDark
            ? ScottPlot.Color.FromHex("#0A0C0F")
            : ScottPlot.Color.FromHex("#FFFFFF");
        _plot.FigureBackground.Color = bgColor;
        _plot.DataBackground.Color = bgColor;

        // Grid
        var gridColor = isDark
            ? ScottPlot.Color.FromHex("#FFFFFF14")
            : ScottPlot.Color.FromHex("#6B728264");
        _plot.Grid.MajorLineColor = gridColor;
        _plot.Grid.MinorLineColor = gridColor.WithAlpha(80);

        // Axes
        var labelColor = isDark
            ? ScottPlot.Color.FromHex("#E6E6E6")
            : ScottPlot.Color.FromHex("#0F172A");
        var tickColor = isDark
            ? ScottPlot.Color.FromHex("#FFFFFF8C")
            : ScottPlot.Color.FromHex("#6B7280B4");

        _plot.Axes.Bottom.Label.ForeColor = labelColor;
        _plot.Axes.Left.Label.ForeColor = labelColor;
        _plot.Axes.Bottom.TickLabelStyle.ForeColor = labelColor;
        _plot.Axes.Left.TickLabelStyle.ForeColor = labelColor;
        _plot.Axes.Bottom.MajorTickStyle.Color = tickColor;
        _plot.Axes.Left.MajorTickStyle.Color = tickColor;
        _plot.Axes.Bottom.MinorTickStyle.Color = tickColor.WithAlpha(80);
        _plot.Axes.Left.MinorTickStyle.Color = tickColor.WithAlpha(80);
        _plot.Axes.Bottom.FrameLineStyle.Color = tickColor;
        _plot.Axes.Left.FrameLineStyle.Color = tickColor;
        _plot.Axes.Right.FrameLineStyle.Color = tickColor.WithAlpha(40);
        _plot.Axes.Top.FrameLineStyle.Color = tickColor.WithAlpha(40);

        // Font sizing
        _plot.Axes.Bottom.TickLabelStyle.FontSize = 11;
        _plot.Axes.Left.TickLabelStyle.FontSize = 11;

        // Labels
        _plot.Axes.Bottom.Label.Text = "Sample";
        _plot.Axes.Left.Label.Text = "";

        _plot.HideLegend();
    }

    /// <summary>
    /// Re-applies theme colors after a theme switch.
    /// </summary>
    public void UpdateThemeColors()
    {
        if (_disposed || _plot == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            ConfigurePlotAppearance();
            _refreshCallback?.Invoke();
        }, DispatcherPriority.Render);
    }

    #endregion
}