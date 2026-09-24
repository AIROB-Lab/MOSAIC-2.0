using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Basics;
using MOSAIC.Services;
using MOSAIC.Visualization;
using SkiaSharp;

namespace MOSAIC.Visualization.Heatmap;

/// <summary>Top-level color scaling mode: one range for all cells, or a separate range per cell.</summary>
public enum ColorScaleMode { Global, PerSensor }

/// <summary>Per-sensor scaling strategy: adaptive RMS envelope, or caller-supplied fixed ranges.</summary>
public enum PerSensorScaling { AdaptiveRms, FixedRanges }

/// <summary>
/// Real-time heatmap visualization backed by LiveCharts <see cref="HeatSeries{T}"/>.
/// Renders a rows × columns color grid where each cell's color encodes a scalar value
/// mapped through a pluggable <see cref="IHeatmapScaler"/>.
///
/// <para><b>Data flow:</b> Call <see cref="EnqueueFrame(ReadOnlySpan{double})"/> or
/// <see cref="EnqueueFrame(Vector{double})"/> from any thread. The newest frame is handed off via a
/// lock-free <see cref="LatestFrameSlot"/>; the internal <see cref="VisualizationTimer"/> tick reads
/// it on the UI thread at 60 fps, maps values through the active scaler, and nudges LiveCharts to repaint.</para>
///
/// <para><b>Grid layout:</b> Defaults to 8 × 16 (128 cells). Call <see cref="ConfigureGrid"/>
/// to change layout. When an incoming frame has a different length to the current grid,
/// the grid auto-reconfigures to a single-row 1 × N layout on the next UI tick.</para>
///
/// <para><b>Scaling:</b> Set via <see cref="UseGlobalAuto"/>, <see cref="UseGlobalFixed"/>,
/// <see cref="UsePerSensorFixedForAll"/>, <see cref="UsePerSensorFixed"/>, or
/// <see cref="UsePerSensorAdaptiveRms"/>. Default after construction is no scaler —
/// call <see cref="UseGlobalAuto"/> (or another mode) before feeding data, or use
/// <see cref="BlockVisualization"/> which calls <c>UseGlobalAuto()</c> automatically.</para>
///
/// <para><b>Colormap:</b> Set via extension methods in <c>HeatmapColormaps</c>
/// (e.g., <c>hm.UseViridis()</c>). Default is a 4-stop Viridis approximation.</para>
///
/// <para><b>Colorbar:</b> <see cref="ColorbarBitmap"/>, <see cref="ColorbarMinLabel"/>,
/// <see cref="ColorbarMaxLabel"/>, and <see cref="ColorbarCaption"/> are updated each
/// flush and bound by <see cref="HeatMapView"/> to the legend sidebar.</para>
///
/// <para><b>Lifecycle:</b> <see cref="VisualizationPanel"/> (or <see cref="HeatMapView"/>
/// directly) calls <see cref="Pause"/> and <see cref="Resume"/>. Do not call these
/// yourself when using <see cref="BlockVisualization"/>.</para>
/// </summary>
public partial class HeatMapMonitor : ObservableObject, IDisposable, IMonitor
{
    // threading/infra
    private readonly Action _timerCallback;
    // Latest-only handoff: the heatmap renders only the newest field each flush, so a single-frame
    // slot replaces the ConcurrentQueue<double[]> + ArrayPool churn.
    private LatestFrameSlot? _slot;
    private double[] _producerScratch = Array.Empty<double>(); // for the Vector overload
    private double[] _consumerFrame = Array.Empty<double>();    // consumer-side latest read
    private volatile bool _disposed;
    private volatile bool _isActive;
    public bool IsActive => _isActive;
    private volatile bool _isUpdating;
    // Global gate: set only after the CartesianChart fires AttachedToVisualTree.
    // Cleared by Pause() so every new chart attach re-gates correctly.
    private volatile bool _chartReady;
    private readonly object _sync;
    private int _flushScheduled;
    private int _reconfigureScheduled;
    private int _pendingCellCount;


    // grid & mapping
    [ObservableProperty]
    private int _rows = 8;

    [ObservableProperty]
    private int _columns = 16;

    public int[]? IndexMap { get; set; }

    // chart binding
    [ObservableProperty]
    private List<ISeries> _series = new();

    [ObservableProperty]
    private Axis[] _xAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private Axis[] _yAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private object _syncContext;

    // series & data
    private HeatSeries<WeightedPoint>? _heatSeries;
    private WeightedPoint[]? _points;

    // scaler
    private IHeatmapScaler? _scaler;

    // optional transform for incoming values
    public Func<double, double>? ValueTransform { get; set; } = null;

    // anchors to pin color domain to 0..1 (avoid collapse in tiny grids)
    public bool PinColorDomain { get; set; } = true;
    private WeightedPoint? _anchorMin, _anchorMax;

    // legend & colorbar
    public Bitmap? ColorbarBitmap { get; private set; }
    public string ColorbarMinLabel { get; private set; } = "min";
    public string ColorbarMaxLabel { get; private set; } = "max";
    public string ColorbarCaption  { get; private set; } = "";
    public bool ColorbarHighAtTop { get; set; } = true;

    public HeatMapMonitor()
    {
        _sync = new object();
        _syncContext = _sync;
        _timerCallback = RequestFlush;

        InitializeChart();
        RebuildGrid();
    }

    /// <summary>
    /// Reconfigures the grid dimensions and optional channel mapping.
    /// Safe to call from any thread — marshals to the UI thread internally.
    /// Drains the queue and resets scaler state for the new cell count.
    /// </summary>
    /// <param name="rows">Number of rows (must be ≥ 1).</param>
    /// <param name="cols">Number of columns (must be ≥ 1).</param>
    /// <param name="indexMap">
    /// Optional remapping: value at input index <c>i</c> goes to grid cell <c>indexMap[i]</c>.
    /// Length must equal <c>rows × cols</c>. Pass <see langword="null"/> for identity mapping.
    /// </param>
    public void ConfigureGrid(int rows, int cols, int[]? indexMap = null)
    {
        if (rows <= 0 || cols <= 0) return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ConfigureGrid(rows, cols, indexMap), DispatcherPriority.Render);
            return;
        }

        lock (_sync)
        {
            var wasActive = _isActive;
            _isActive = false;
            _isUpdating = true;

            try
            {
                DrainQueue();
                Rows = rows; Columns = cols; IndexMap = indexMap;
                RebuildGrid();
                _scaler?.Configure(Rows * Columns);
            }
            finally
            {
                _isUpdating = false;
                _isActive = wasActive;
            }
        }
    }

    /// <summary>
    /// Stops data ingestion and unsubscribes from the visualization timer.
    /// Any pending frame is discarded.
    /// Called automatically by <see cref="VisualizationPanel"/> when the heatmap toggle is turned off.
    /// </summary>
    public void Pause()
    {
        _isActive = false;
        _chartReady = false;
        VisualizationTimer.Instance.Unsubscribe(_timerCallback);
        DrainQueue();
    }

    /// <summary>
    /// Subscribes to the visualization timer (60 fps) and discards any stale frame.
    /// Called automatically by <see cref="VisualizationPanel"/> when the heatmap toggle is turned on.
    /// Do not call this yourself when using <see cref="BlockVisualization"/>.
    /// </summary>
    public void Resume()
    {
        if (_disposed) return;

        // Drop stale data that accumulated while paused
        DrainQueue();

        _isActive = true;
        VisualizationTimer.Instance.Subscribe(_timerCallback, VisualizationTimer.TickRate.Fps60);
    }

    /// <summary>
    /// Called by <see cref="HeatMapView"/> from the <see cref="LiveChartsCore.SkiaSharpView.Avalonia.CartesianChart"/>
    /// <c>AttachedToVisualTree</c> event (at <c>Loaded</c> priority), confirming LiveCharts
    /// has completed at least one <c>Measure()</c> pass and its internal state is safe.
    /// This is the single global gate against premature flush crashes.
    /// </summary>
    public void NotifyChartReady()
    {
        _chartReady = true;
    }

    /// <summary>
    /// Resets chart to clean state. Called by VisualizationPanel on attach/toggle.
    /// </summary>
    public void ResetData()
    {
        if (_disposed) return;

        if (Dispatcher.UIThread.CheckAccess())
        {
            ResetDataCore();
        }
        else
        {
            Dispatcher.UIThread.Post(ResetDataCore, DispatcherPriority.Render);
        }
    }

    private void ResetDataCore()
    {
        if (_disposed) return;

        lock (_sync)
        {
            _isUpdating = true;
            try
            {
                DrainQueue();
                RebuildGrid();
            }
            finally
            {
                _isUpdating = false;
            }
        }
    }

    /// <summary>
    /// Enqueues a frame of sensor values for the next render tick.
    /// Thread-safe; call from any thread (typically the processing thread inside <c>OnReceive</c>).
    /// If the frame length does not match <c>Rows × Columns</c>, the grid auto-reconfigures
    /// to a 1 × N layout on the next UI tick.
    /// </summary>
    /// <param name="values">Sensor values; length must equal <c>Rows × Columns</c>.</param>
    public void EnqueueFrame(ReadOnlySpan<double> values)
    {
        if (_disposed || !_isActive || _isUpdating) return;

        var slot = _slot;
        if (slot is null || slot.Channels != values.Length)
        {
            RequestGridReconfigure(values.Length);
            return;
        }

        slot.Write(values);
    }

    /// <summary>
    /// Enqueues a frame from a MathNet <see cref="Vector{T}"/> of <see cref="double"/>.
    /// Convenience overload for blocks that already have a Vector.
    /// </summary>
    /// <param name="vec">Vector of sensor values; length must equal <c>Rows × Columns</c>.</param>
    public void EnqueueFrame(Vector<double> vec)
    {
        if (_disposed || !_isActive || _isUpdating) return;

        var slot = _slot;
        if (slot is null || slot.Channels != vec.Count)
        {
            RequestGridReconfigure(vec.Count);
            return;
        }

        var arr = vec.AsArray();
        if (arr is not null)
        {
            slot.Write(arr);
        }
        else
        {
            if (_producerScratch.Length != vec.Count) _producerScratch = new double[vec.Count];
            for (int i = 0; i < vec.Count; i++) _producerScratch[i] = vec[i];
            slot.Write(_producerScratch);
        }
    }

    /// <summary>
    /// Auto-reconfigures grid when incoming data has a different cell count.
    /// Maps to a 1×N grid (single row) by default — callers can use
    /// ConfigureGrid() for custom row/column layouts.
    /// </summary>
    private void RequestGridReconfigure(int newCellCount)
    {
        if (_disposed || newCellCount <= 0) return;

        Volatile.Write(ref _pendingCellCount, newCellCount);
        if (Interlocked.Exchange(ref _reconfigureScheduled, 1) == 1) return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _reconfigureScheduled, 0);
            if (_disposed) return;

            var cellCount = Volatile.Read(ref _pendingCellCount);

            lock (_sync)
            {
                var wasActive = _isActive;
                _isActive = false;
                _isUpdating = true;

                try
                {
                    DrainQueue();
                    // Default to single-row layout; callers can override with ConfigureGrid()
                    Rows = 1;
                    Columns = cellCount;
                    RebuildGrid();
                    _scaler?.Configure(cellCount);
                }
                finally
                {
                    _isUpdating = false;
                    _isActive = wasActive;
                }
            }
        }, DispatcherPriority.Render);
    }

    // ---- Mode helpers ----

    /// <summary>
    /// Sets scaler to <see cref="GlobalAutoScaler"/> (expand fast, contract slow).
    /// Best when the signal range is unknown or slowly drifting.
    /// This is the default when using <see cref="BlockVisualization"/>.
    /// </summary>
    /// <param name="expandAlpha">EMA weight when range expands (0..1). Higher = faster tracking of peaks.</param>
    /// <param name="contractAlpha">EMA weight when range contracts (0..1). Lower = steadier baseline.</param>
    /// <param name="minSpan">Minimum span to avoid color collapse for constant signals.</param>
    public void UseGlobalAuto(double expandAlpha = 0.6, double contractAlpha = 0.08, double minSpan = 1e-9)
    {
        _scaler = new GlobalAutoScaler(expandAlpha, contractAlpha, minSpan);
        _scaler.Configure(Rows * Columns);
    }

    /// <summary>
    /// Sets scaler to <see cref="GlobalFixedScaler"/>: maps [<paramref name="min"/>..<paramref name="max"/>]
    /// uniformly to [0..1] for every cell. Best for signals with a known stable range (e.g., ±1 V, 0..100%).
    /// </summary>
    /// <param name="min">Minimum value — mapped to color index 0 (bottom of colormap).</param>
    /// <param name="max">Maximum value — mapped to color index 1 (top of colormap).</param>
    public void UseGlobalFixed(double min, double max)
    {
        _scaler = new GlobalFixedScaler(min, max);
        _scaler.Configure(Rows * Columns);
    }

    /// <summary>
    /// Sets scaler to <see cref="PerSensorFixedScaler"/> with the same [min, max] for every cell.
    /// Each cell is scaled independently but using the same range.
    /// Use <see cref="UsePerSensorFixed"/> if cells have different calibrated ranges.
    /// </summary>
    /// <param name="min">Fixed minimum for every cell.</param>
    /// <param name="max">Fixed maximum for every cell.</param>
    /// <param name="epsilon">Minimum span guard to avoid divide-by-zero.</param>
    public void UsePerSensorFixedForAll(double min, double max, double epsilon = 1e-9)
    {
        if (!(min < max)) throw new ArgumentException("min must be < max");
        int n = Rows * Columns;
        var mins = Enumerable.Repeat(min, n).ToArray();
        var maxs = Enumerable.Repeat(max, n).ToArray();
        UsePerSensorFixed(mins, maxs, epsilon);
    }

    /// <summary>
    /// Sets scaler to <see cref="PerSensorFixedScaler"/> with individual [min, max] per cell.
    /// Each element of <paramref name="mins"/>/<paramref name="maxs"/> corresponds to one grid cell
    /// in row-major order. Useful for calibrated multi-electrode or multi-sensor arrays.
    /// </summary>
    /// <param name="mins">Per-cell minimum values. Length must equal <c>Rows × Columns</c>.</param>
    /// <param name="maxs">Per-cell maximum values. Length must equal <c>Rows × Columns</c>.</param>
    /// <param name="epsilon">Minimum span guard.</param>
    public void UsePerSensorFixed(double[] mins, double[] maxs, double epsilon = 1e-9)
    {
        if (mins is null || maxs is null || mins.Length != Rows * Columns || maxs.Length != Rows * Columns)
            throw new ArgumentException("mins/maxs must match Rows*Columns.");
        _scaler = new PerSensorFixedScaler(mins, maxs, epsilon);
        _scaler.Configure(Rows * Columns);
    }

    /// <summary>
    /// Sets scaler to <see cref="PerSensorAdaptiveRmsScaler"/>: each cell independently tracks
    /// its own RMS envelope and maps [-RMS*k .. +RMS*k] → [0..1].
    /// Best for zero-mean signals of unknown amplitude (EMG, EEG, accelerometer).
    /// </summary>
    /// <param name="emaAlpha">EMA weight for RMS update (0..1). Higher = faster envelope tracking.</param>
    /// <param name="k">Scale multiplier relative to RMS. √2 clips sine-wave peaks at ±1.</param>
    /// <param name="halfLifeSeconds">Peak-hold decay half-life. Shorter = faster re-centering after bursts.</param>
    public void UsePerSensorAdaptiveRms(double emaAlpha = 0.12, double k = 1.414, double halfLifeSeconds = 0.6)
    {
        var scaler = new PerSensorAdaptiveRmsScaler
        {
            EmaAlpha = emaAlpha,
            K = k,
            HalfLifeSeconds = halfLifeSeconds
        };
        _scaler = scaler;
        _scaler.Configure(Rows * Columns);
    }


    /// <summary>
    /// Applies a custom colormap directly. Prefer the extension methods in <c>HeatmapColormaps</c>
    /// (e.g., <c>hm.UseViridis()</c>) over calling this directly.
    /// Safe to call from any thread.
    /// </summary>
    /// <param name="colors">Array of SkiaSharp colors from low (index 0) to high (last index).</param>
    /// <param name="stops01">
    /// Optional non-uniform stop positions in [0..1], same length as <paramref name="colors"/>.
    /// Pass <see langword="null"/> for uniform spacing.
    /// </param>
    public void SetColormap(SKColor[] colors, double[]? stops01 = null)
    {
        if (_disposed) return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            // Capture arrays for the closure
            var colorsCopy = (SKColor[])colors.Clone();
            var stopsCopy = stops01 is not null ? (double[])stops01.Clone() : null;
            Dispatcher.UIThread.Post(() => SetColormapCore(colorsCopy, stopsCopy), DispatcherPriority.Render);
            return;
        }

        SetColormapCore(colors, stops01);
    }

    private void SetColormapCore(SKColor[] colors, double[]? stops01)
    {
        if (_disposed || _heatSeries is null) return;

        lock (_sync)
        {
            var map = colors.Select(c => c.AsLvcColor()).ToArray();
            _heatSeries.HeatMap = map;

            _heatSeries.ColorStops = stops01 is null
                ? (map.Length == 1
                    ? new[] { 0.0 }
                    : Enumerable.Range(0, map.Length).Select(i => i / (double)(map.Length - 1)).ToArray())
                : (stops01.Length == map.Length
                    ? stops01
                    : Enumerable.Range(0, map.Length).Select(i => i / (double)(map.Length - 1)).ToArray());
        }

        // Nudge LiveCharts to repaint — it doesn't observe HeatMap/ColorStops changes directly
        OnPropertyChanged(nameof(Series));
        RebuildColorbar();
    }


    public void UpdateThemeColors()
    {
        if (_disposed) return;

        Dispatcher.UIThread.Post(() =>
        {
            lock (_sync)
            {
                var labelColor = ThemeHelper.ChartAxisLabel;
                var gridColor = ThemeHelper.ChartGrid;

                if (_xAxes.Length > 0)
                {
                    _xAxes[0].LabelsPaint = new SolidColorPaint(labelColor) { IsAntialias = true };
                    _xAxes[0].SeparatorsPaint = new SolidColorPaint(gridColor) { IsAntialias = true };
                }
                if (_yAxes.Length > 0)
                {
                    _yAxes[0].LabelsPaint = new SolidColorPaint(labelColor) { IsAntialias = true };
                    _yAxes[0].SeparatorsPaint = new SolidColorPaint(gridColor) { IsAntialias = true };
                }

                OnPropertyChanged(nameof(XAxes));
                OnPropertyChanged(nameof(YAxes));
            }
        }, DispatcherPriority.Render);
    }
    // ------------------------------- Internals --------------------------------

    private void InitializeChart()
    {
        var labelColor = ThemeHelper.ChartAxisLabel;
        var gridColor = ThemeHelper.ChartGrid;

        _xAxes = new[]
        {
            new Axis
            {
                Name = null,
                LabelsPaint = new SolidColorPaint(labelColor) { IsAntialias = true },
                SeparatorsPaint = new SolidColorPaint(gridColor) { IsAntialias = true },
                MinLimit = -0.5, MaxLimit = Columns - 0.5,
                ForceStepToMin = true, MinStep = 1,
                AnimationsSpeed = TimeSpan.Zero,
            }
        };
        _yAxes = new[]
        {
            new Axis
            {
                Name = null,
                LabelsPaint = new SolidColorPaint(labelColor) { IsAntialias = true },
                SeparatorsPaint = new SolidColorPaint(gridColor) { IsAntialias = true },
                MinLimit = -0.5, MaxLimit = Rows - 0.5,
                ForceStepToMin = true, MinStep = 1,
                AnimationsSpeed = TimeSpan.Zero,
            }
        };

        var defaultMap = new[]
        {
            new SKColor(68, 1, 84),
            new SKColor(49,104,142),
            new SKColor(53,183,121),
            new SKColor(253,231,37)
        };

        _heatSeries = new HeatSeries<WeightedPoint>
        {
            Values = Array.Empty<WeightedPoint>(),
            PointPadding = new LiveChartsCore.Drawing.Padding(0),
            HeatMap = defaultMap.Select(c => c.AsLvcColor()).ToArray(),
            ColorStops = new[] { 0.0, 0.33, 0.66, 1.0 },
            DataLabelsPaint = null,
            AnimationsSpeed = TimeSpan.Zero,
            EasingFunction = null
        };

        _series = new List<ISeries> { _heatSeries };
    }

    private void RebuildGrid()
    {
        lock (_sync)
        {
            _points = new WeightedPoint[Rows * Columns];
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Columns; c++)
                    _points[r * Columns + c] = new WeightedPoint(c, r, 0);

            // (Re)allocate the latest-frame slot for the current cell count.
            _slot = (Rows * Columns) > 0 ? new LatestFrameSlot(Rows * Columns) : null;

            if (_heatSeries != null)
                _heatSeries.Values = new ObservableCollection<WeightedPoint>(_points);

            if (_xAxes.Length > 0) { _xAxes[0].MinLimit = -0.5; _xAxes[0].MaxLimit = Columns - 0.5; }
            if (_yAxes.Length > 0) { _yAxes[0].MinLimit = -0.5; _yAxes[0].MaxLimit = Rows    - 0.5; }

            _anchorMin = _anchorMax = null;
            if (PinColorDomain && _heatSeries?.Values is ObservableCollection<WeightedPoint> oc)
            {
                _anchorMin = new WeightedPoint(-9999, -9999, 0.0);
                _anchorMax = new WeightedPoint(-9998, -9998, 1.0);
                oc.Add(_anchorMin);
                oc.Add(_anchorMax);
                _heatSeries.DataPadding = new LiveChartsCore.Drawing.LvcPoint(0, 0);
            }

            _scaler?.Configure(Rows * Columns);

            // Notify LiveCharts that axes and series changed
            OnPropertyChanged(nameof(XAxes));
            OnPropertyChanged(nameof(YAxes));
            OnPropertyChanged(nameof(Series));
        }
    }

    private void RequestFlush()
    {
        if (_disposed || !_isActive || !_chartReady || _isUpdating) return;
        if (_slot is null || !_slot.HasData) return;

        if (System.Threading.Interlocked.Exchange(ref _flushScheduled, 1) == 1) return;

        Dispatcher.UIThread.Post(() =>
        {
            System.Threading.Interlocked.Exchange(ref _flushScheduled, 0);
            FlushToChart();
        }, DispatcherPriority.Render);
    }

    private void FlushToChart()
    {
        if (_disposed || !_isActive || !_chartReady || _isUpdating || _points is null || _heatSeries is null || _scaler is null) return;
        var slot = _slot;
        if (slot is null) return;

        _isUpdating = true;
        try
        {
            if (_consumerFrame.Length != slot.Channels) _consumerFrame = new double[slot.Channels];
            if (!slot.TryReadInto(_consumerFrame)) return; // no new frame since last flush

            lock (_sync)
            {
                if (_disposed || !_isActive || _points is null) return;

                _scaler.MapFrame(_consumerFrame, _points, IndexMap, ValueTransform, out var legend);

                if (PinColorDomain && _anchorMin is not null && _anchorMax is not null)
                {
                    _anchorMin.Weight = 0.0;
                    _anchorMax.Weight = 1.0;
                }

                // WeightedPoint implements INotifyPropertyChanged (LiveChartsCore.Defaults),
                // so the in-place Weight writes in MapFrame above already notify the chart.
                // LiveCharts coalesces them into one throttled repaint (~10 ms) and re-colors
                // the existing cells in place. Avoid structural collection changes here because
                // they force a full clear and redraw.

                ColorbarMinLabel = legend.MinLabel;
                ColorbarMaxLabel = legend.MaxLabel;
                ColorbarCaption  = legend.Caption;
                OnPropertyChanged(nameof(ColorbarMinLabel));
                OnPropertyChanged(nameof(ColorbarMaxLabel));
                OnPropertyChanged(nameof(ColorbarCaption));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HeatMap] FlushToChart error: {ex.Message}");
        }
        finally
        {
            _isUpdating = false;
        }
    }


    /// <summary>
    /// Rebuilds <see cref="ColorbarBitmap"/> from the current colormap using a SkiaSharp linear gradient.
    /// Called automatically after <see cref="SetColormap"/> and when a new <see cref="HeatMapMonitor"/> is bound
    /// by <see cref="HeatMapView"/>. Only needs to be called manually if you change the colormap
    /// outside of the extension methods.
    /// </summary>
    /// <param name="width">Pixel width of the colorbar image.</param>
    /// <param name="height">Pixel height of the colorbar image.</param>
    /// <param name="vertical">
    /// <see langword="true"/> for a vertical bar (default); <see langword="false"/> for horizontal.
    /// </param>
    public void RebuildColorbar(int width = 18, int height = 180, bool vertical = true)
    {
        if (_disposed || _heatSeries is null || _heatSeries.HeatMap is null || _heatSeries.HeatMap.Length == 0) return;

        try
        {
            var allColors = _heatSeries.HeatMap.Select(c => new SKColor(c.R, c.G, c.B, c.A)).ToArray();

            // SkiaSharp linear gradient works best with a reasonable number of stops.
            // If we have 256+ colors, sample down to at most 64 anchors for the colorbar image.
            SKColor[] colors;
            float[] stops;

            const int maxStops = 64;
            if (allColors.Length <= maxStops)
            {
                colors = allColors;
                stops = Enumerable.Range(0, colors.Length)
                    .Select(i => i / (float)(colors.Length - 1))
                    .ToArray();
            }
            else
            {
                colors = new SKColor[maxStops];
                stops = new float[maxStops];
                for (int i = 0; i < maxStops; i++)
                {
                    float t = i / (float)(maxStops - 1);
                    int srcIdx = Math.Min((int)(t * (allColors.Length - 1)), allColors.Length - 1);
                    colors[i] = allColors[srcIdx];
                    stops[i] = t;
                }
            }

            using var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bmp);
            using var paint = new SKPaint { IsAntialias = true };

            SKPoint start, end;
            if (vertical)
            {
                start = ColorbarHighAtTop ? new SKPoint(0, height) : new SKPoint(0, 0);
                end   = ColorbarHighAtTop ? new SKPoint(0, 0)      : new SKPoint(0, height);
            }
            else
            {
                start = new SKPoint(0, 0);
                end   = new SKPoint(width, 0);
            }

            paint.Shader = SKShader.CreateLinearGradient(start, end, colors, stops, SKShaderTileMode.Clamp);
            canvas.Clear(new SKColor(0, 0, 0, 0));
            canvas.DrawRect(new SKRect(0, 0, width, height), paint);

            using var img  = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = data.AsStream();
            ColorbarBitmap = new Bitmap(stream);
            OnPropertyChanged(nameof(ColorbarBitmap));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HeatMap] RebuildColorbar error: {ex.Message}");
        }
    }


    private void DrainQueue() => _slot?.Clear();


    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isActive = false;
        VisualizationTimer.Instance.Unsubscribe(_timerCallback);
        DrainQueue();
        _points = null;
        _heatSeries = null;
    }
}
