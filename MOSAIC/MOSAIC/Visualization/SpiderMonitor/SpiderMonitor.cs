using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using MOSAIC.Components.Basics;
using MOSAIC.Services;
using MOSAIC.Visualization.ScopeMonitor;
using SkiaSharp;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.Visualization.SpiderMonitor;

/// <summary>
/// Real-time polar (spider/radar) monitor: renders the latest multichannel sample as a closed
/// polar trace, with optional EMA smoothing, per-channel normalization, and a decaying peak
/// envelope. The newest sample is handed off via a lock-free <see cref="LatestFrameSlot"/> and
/// one snapshot is flushed per timer tick.
/// Lifecycle is <c>Pause()</c> / <c>Resume()</c> / <c>ResetData()</c> / <c>Dispose()</c>.
/// </summary>
public partial class SpiderMonitor : ObservableObject, IDisposable, IMonitor
{
    #region Fields

    private readonly Action _timerCallback;

    // Latest-only handoff: the spider renders only the newest sample, so a single-frame slot
    // replaces the old per-sample ConcurrentQueue<double[]> + ToArray() allocation.
    private LatestFrameSlot? _slot;
    private double[] _producerScratch = Array.Empty<double>(); // producer-side EMA output
    private double[] _consumerFrame = Array.Empty<double>();    // consumer-side latest read

    private volatile bool _disposed;

    private volatile bool _isActive;
    public bool IsActive => _isActive;

    private volatile bool _isUpdating;

    // Global gate: set only after the PolarChart fires AttachedToVisualTree.
    // Cleared by Pause() so every new chart attach re-gates correctly.
    private volatile bool _chartReady;

    private int _flushScheduled;

    private int _numberOfChannels;

    private VisualizationTimer.TickRate _tickRate = VisualizationTimer.TickRate.Fps30;

    private int _reconfigureScheduled;

    private int _pendingChannelCount;

    private BatchObservableCollection<double> _values = new();

    // EMA smoothing
    // Smooths jittery high-rate signals so the spider shape is readable.
    // Alpha 0 = no smoothing (raw), 1 = frozen. 0.3 is a good default for
    // 30fps display with 500-2000 Hz input.
    private double _emaAlpha = 0.3;

    private double[]? _emaState;

    // Per-channel normalization
    // When enabled, each channel is independently scaled to [0, 1] using its
    // observed running min/max. This lets mixed-amplitude sensors (e.g., filtered
    // EMG at ±1 alongside unfiltered at ±0.005) all be visible on the same plot.
    private bool _normalizePerChannel;

    private double[]? _runningMin;

    private double[]? _runningMax;

    // Peak envelope
    // A second (semi-transparent) series that shows the recent peak shape.
    // Decays multiplicatively each flush so it fades over ~1-2 seconds.
    private bool _showPeakEnvelope = false;

    private double _peakDecay = 0.97;     // per-flush decay factor

    private double[]? _peakValues;

    private BatchObservableCollection<double> _peakCollection = new();

    private bool _autoScale = true;

    private double _fixedMin = double.NaN;

    private double _fixedMax = double.NaN;

    private double _lastRadiusMax = double.NaN;

    #endregion

    #region Observable properties

    [ObservableProperty]
    private List<ISeries> _series = new();

    [ObservableProperty]
    private PolarAxis[] _radiusAxes = Array.Empty<PolarAxis>();

    [ObservableProperty]
    private PolarAxis[] _angleAxes = Array.Empty<PolarAxis>();

    [ObservableProperty]
    private bool _isReading = true;

    #endregion

    #region Public properties

    // LiveCharts sync object — bind to PolarChart.SyncContext
    public object Sync { get; } = new();

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

    public int ChannelCount => _numberOfChannels;

    public bool IsAutoScale => _autoScale;

    /// <summary>
    /// EMA smoothing factor. 0 = no smoothing (raw samples), higher = smoother.
    /// Good range is 0.1–0.5 for 30fps display. Default 0.3.
    /// </summary>
    public double EmaAlpha
    {
        get => _emaAlpha;
        set => _emaAlpha = Math.Clamp(value, 0.0, 0.99);
    }

    /// <summary>
    /// Enable per-channel normalization. Each channel is independently scaled
    /// to [0, 1] based on its observed running min/max. Useful when channels
    /// have vastly different amplitudes (e.g., filtered vs unfiltered EMG).
    /// </summary>
    public bool NormalizePerChannel
    {
        get => _normalizePerChannel;
        set
        {
            _normalizePerChannel = value;
            // Reset running stats when toggling so stale extremes don't persist
            if (_runningMin != null) Array.Fill(_runningMin, double.MaxValue);
            if (_runningMax != null) Array.Fill(_runningMax, double.MinValue);
            _lastRadiusMax = double.NaN;
        }
    }

    /// <summary>
    /// Show/hide the decaying peak envelope behind the main trace.
    /// </summary>
    public bool ShowPeakEnvelope
    {
        get => _showPeakEnvelope;
        set
        {
            _showPeakEnvelope = value;
            // Update visibility on the existing series without replacing it
            Dispatcher.UIThread.Post(() =>
            {
                lock (Sync)
                {
                    if (Series.Count > 0 && Series[0] is PolarLineSeries<double> peakSeries)
                        peakSeries.IsVisible = value;
                    OnPropertyChanged(nameof(Series));
                }
            }, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Peak decay factor per flush. 0.97 ≈ 1 second hold at 30fps, 0.90 ≈ fast decay.
    /// </summary>
    public double PeakDecay
    {
        get => _peakDecay;
        set => _peakDecay = Math.Clamp(value, 0.0, 0.999);
    }

    #endregion

    #region Construction

    public SpiderMonitor()
    {
        _timerCallback = RequestFlush;
        SpiderInit(0);
    }

    #endregion

    #region Public API

    public void EnqueueData(Vector data)
    {
        if (_disposed || data is null || data.Count == 0) return;
        if (_isUpdating) return;
        if (!_isActive) return;

        var slot = _slot;
        if (slot is null || slot.Channels != data.Count)
        {
            RequestChannelReconfigure(data.Count);
            return;
        }

        if (_producerScratch.Length != data.Count) _producerScratch = new double[data.Count];
        var scratch = _producerScratch;
        var src = data.AsArray();

        // Capture array references ONCE. This method runs on the producer thread,
        // while ApplyChannelCountInternal / ResetDataCore may reallocate or clear
        // these same arrays on the UI thread. Working against locals means a concurrent
        // reallocation can't change the length we already validated — worst case we
        // update a soon-to-be-discarded array, which is harmless.
        var ema = _emaState;
        var runningMin = _runningMin;
        var runningMax = _runningMax;

        bool doEma = ema != null && _emaAlpha > 0 && ema.Length == scratch.Length;
        bool doMinMax = runningMin != null && runningMax != null
                        && runningMin.Length == scratch.Length && runningMax.Length == scratch.Length;

        for (int i = 0; i < scratch.Length; i++)
        {
            double v = src is not null ? src[i] : data[i];
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0.0;

            // Apply EMA smoothing to reduce jitter on the spider shape.
            if (doEma)
            {
                ema![i] = _emaAlpha * ema[i] + (1.0 - _emaAlpha) * v;
                v = ema[i];
            }

            scratch[i] = v;

            // Track per-channel running min/max for normalization mode.
            if (doMinMax)
            {
                if (v < runningMin![i]) runningMin[i] = v;
                if (v > runningMax![i]) runningMax[i] = v;
            }
        }

        slot.Write(scratch);
    }

    public void SpiderInit(int count = 0)
    {
        if (_disposed) return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SpiderInit(count), DispatcherPriority.Render);
            return;
        }

        lock (Sync)
        {
            var wasActive = _isActive;
            _isActive = false;
            _isUpdating = true;

            try
            {
                _slot?.Clear();
                ApplyChannelCountInternal(count);
            }
            finally
            {
                _isUpdating = false;
                _isActive = wasActive;
            }
        }
    }

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

    public void Pause()
    {
        _isActive = false;
        _chartReady = false;
        _values.IsChartReady = false;
        _peakCollection.IsChartReady = false;
        VisualizationTimer.Instance.Unsubscribe(_timerCallback);
        _slot?.Clear();
    }

    public void Resume()
    {
        if (_disposed) return;

        // Drop any stale frame that arrived while paused
        _slot?.Clear();

        // The View guarantees the chart exists before calling Resume().
        // Set the gate here to avoid race with the chart's own AttachedToVisualTree
        // deferred callback (which may fire after data starts arriving).
        _chartReady = true;
        _values.IsChartReady = true;
        _peakCollection.IsChartReady = true;
        _isActive = true;
        VisualizationTimer.Instance.Subscribe(_timerCallback, _tickRate);
    }

    /// <summary>
    /// Called by <see cref="SpiderMonitorView"/> from the <see cref="LiveChartsCore.SkiaSharpView.Avalonia.PolarChart"/>
    /// <c>AttachedToVisualTree</c> event (at <c>Loaded</c> priority), confirming LiveCharts
    /// has completed at least one <c>Measure()</c> pass and its internal state is safe.
    /// This is the single global gate against premature flush crashes.
    /// </summary>
    public void NotifyChartReady()
    {
        _chartReady = true;
        _values.IsChartReady = true;
        _peakCollection.IsChartReady = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Pause();

        lock (Sync)
        {
            _values.Clear();
            _peakCollection.Clear();
            Series.Clear();
        }

        _slot?.Clear();
    }

    /// <summary>Enable autoscaling radius axis from data (default).</summary>
    public void SetAutoScale()
    {
        _autoScale = true;
        _fixedMin = double.NaN;
        _fixedMax = double.NaN;
        _lastRadiusMax = double.NaN;
    }

    /// <summary>Fix radius axis to a specific range. Data outside is clipped visually.</summary>
    public void SetFixedRange(double min, double max)
    {
        if (min >= max) return;
        _autoScale = false;
        _fixedMin = min;
        _fixedMax = max;
        _lastRadiusMax = max;

        if (RadiusAxes.Length > 0)
        {
            RadiusAxes[0].MinLimit = min;
            RadiusAxes[0].MaxLimit = max;
            OnPropertyChanged(nameof(RadiusAxes));
        }
    }

    public void UpdateThemeColors()
    {
        if (_disposed) return;

        Dispatcher.UIThread.Post(() =>
        {
            lock (Sync)
            {
                var label = GetAxisLabelColor();
                var grid = GetGridColor();
                var dash = new DashEffect([3, 3]);

                if (RadiusAxes.Length > 0)
                {
                    var rAxis = RadiusAxes[0];
                    rAxis.TextSize = GetRadiusLabelSize();
                    rAxis.LabelsPaint = new SolidColorPaint(label) { IsAntialias = true };
                    rAxis.SeparatorsPaint = new SolidColorPaint(grid)
                    {
                        StrokeThickness = 1.0f,
                        PathEffect = dash,
                        IsAntialias = true
                    };
                }

                if (AngleAxes.Length > 0)
                {
                    var aAxis = AngleAxes[0];
                    aAxis.TextSize = GetAngleLabelSize();
                    aAxis.LabelsPaint = new SolidColorPaint(label) { IsAntialias = true };
                    aAxis.SeparatorsPaint = new SolidColorPaint(grid)
                    {
                        StrokeThickness = 1.0f,
                        PathEffect = dash,
                        IsAntialias = true
                    };
                }

                // Update peak envelope color (series 0)
                var peakColor = GetPeakEnvelopeColor();
                if (Series.Count > 0 && Series[0] is PolarLineSeries<double> peakSeries)
                {
                    peakSeries.Stroke = new SolidColorPaint(peakColor) { StrokeThickness = GetPeakStrokeThickness() };
                    peakSeries.Fill = GetPeakFill(peakColor);
                }

                // Update main trace color (series 1)
                var traceColor = GetTraceColor();
                if (Series.Count > 1 && Series[1] is PolarLineSeries<double> ls)
                {
                    ls.Stroke = new SolidColorPaint(traceColor) { StrokeThickness = GetTraceThickness() };
                    ls.Fill = GetTraceFill(traceColor);
                }

                OnPropertyChanged(nameof(RadiusAxes));
                OnPropertyChanged(nameof(AngleAxes));
                OnPropertyChanged(nameof(Series));
            }
        }, DispatcherPriority.Render);
    }

    #endregion

    #region Rendering pipeline

    private void ResetDataCore()
    {
        if (_disposed) return;

        lock (Sync)
        {
            _isUpdating = true;
            try
            {
                _slot?.Clear();

                // Clear the display collections in-place.
                _values.BeginBatch();
                _values.Clear();
                _values.EndBatch();

                _peakCollection.BeginBatch();
                _peakCollection.Clear();
                _peakCollection.EndBatch();

                // Reset EMA and normalization state
                if (_emaState != null) Array.Clear(_emaState);
                if (_peakValues != null) Array.Clear(_peakValues);
                if (_runningMin != null) Array.Fill(_runningMin, double.MaxValue);
                if (_runningMax != null) Array.Fill(_runningMax, double.MinValue);

                // Update peak envelope series (index 0)
                var peakColor = GetPeakEnvelopeColor();
                if (Series.Count > 0 && Series[0] is PolarLineSeries<double> peakSeries)
                {
                    peakSeries.Stroke = new SolidColorPaint(peakColor) { StrokeThickness = GetPeakStrokeThickness() };
                    peakSeries.Fill = GetPeakFill(peakColor);
                    peakSeries.IsVisible = _showPeakEnvelope;
                }

                // Update main trace series (index 1)
                var color = GetTraceColor();
                if (Series.Count > 1 && Series[1] is PolarLineSeries<double> ls)
                {
                    ls.Stroke = new SolidColorPaint(color) { StrokeThickness = GetTraceThickness() };
                    ls.Fill = GetTraceFill(color);
                    ls.GeometryFill = GetGeometryFill(color);
                    ls.GeometrySize = GetGeometrySize();
                }

                InitializeAxes();
            }
            finally
            {
                _isUpdating = false;
            }
        }
    }

    private void RequestChannelReconfigure(int newCount)
    {
        if (_disposed) return;

        Volatile.Write(ref _pendingChannelCount, newCount);
        if (Interlocked.Exchange(ref _reconfigureScheduled, 1) == 1) return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _reconfigureScheduled, 0);
            if (_disposed) return;

            var count = Volatile.Read(ref _pendingChannelCount);

            lock (Sync)
            {
                var wasActive = _isActive;
                _isActive = false;
                _isUpdating = true;

                try
                {
                    _slot?.Clear();
                    ApplyChannelCountInternal(count);
                }
                finally
                {
                    _isUpdating = false;
                    _isActive = wasActive;
                }
            }
        }, DispatcherPriority.Render);
    }

    private void ApplyChannelCountInternal(int count)
    {
        count = Math.Max(0, count);
        _numberOfChannels = count;
        _lastRadiusMax = double.NaN;

        // Initialize EMA, normalization, and peak arrays
        _emaState = new double[count];
        _runningMin = new double[count];
        _runningMax = new double[count];
        _peakValues = new double[count];
        for (int i = 0; i < count; i++)
        {
            _runningMin[i] = double.MaxValue;
            _runningMax[i] = double.MinValue;
        }

        // (Re)allocate the latest-frame slot for the new channel count.
        _slot = count > 0 ? new LatestFrameSlot(count) : null;

        if (Series.Count == 0)
        {
            // First-time initialisation: create the main series + peak envelope series
            var color = GetTraceColor();
            var newValues = new BatchObservableCollection<double>();
            _values = newValues;
            var newPeak = new BatchObservableCollection<double>();
            _peakCollection = newPeak;

            // Propagate chart-ready gate if the chart already exists
            if (_chartReady)
            {
                _values.IsChartReady = true;
                _peakCollection.IsChartReady = true;
            }

            var peakColor = GetPeakEnvelopeColor();

            Series = new List<ISeries>
            {
                // Peak envelope series (drawn first = behind the main trace)
                new PolarLineSeries<double>
                {
                    Values = newPeak,
                    Stroke = new SolidColorPaint(peakColor) { StrokeThickness = GetPeakStrokeThickness() },
                    Fill = GetPeakFill(peakColor),
                    GeometryFill = null,
                    GeometryStroke = null,
                    GeometrySize = 0,
                    LineSmoothness = 0.0,
                    IsClosed = true,
                    AnimationsSpeed = TimeSpan.Zero,
                    IsHoverable = false,
                    IsVisible = _showPeakEnvelope,
                },
                // Main trace series (drawn on top)
                new PolarLineSeries<double>
                {
                    Values = newValues,
                    Stroke = new SolidColorPaint(color) { StrokeThickness = GetTraceThickness() },
                    Fill = GetTraceFill(color),
                    GeometryFill = GetGeometryFill(color),
                    GeometryStroke = null,
                    GeometrySize = GetGeometrySize(),
                    LineSmoothness = 0.0,
                    IsClosed = true,
                    AnimationsSpeed = TimeSpan.Zero,
                    IsHoverable = false,
                },
            };
        }
        else
        {
            // Channel count changed on a live chart: clear data in-place, update appearance
            _values.BeginBatch();
            _values.Clear();
            _values.EndBatch();

            _peakCollection.BeginBatch();
            _peakCollection.Clear();
            _peakCollection.EndBatch();

            var color = GetTraceColor();

            // Update peak envelope series (index 0)
            if (Series.Count > 0 && Series[0] is PolarLineSeries<double> peakSeries)
            {
                var peakColor = GetPeakEnvelopeColor();
                peakSeries.Stroke = new SolidColorPaint(peakColor) { StrokeThickness = GetPeakStrokeThickness() };
                peakSeries.Fill = GetPeakFill(peakColor);
                peakSeries.IsVisible = _showPeakEnvelope;
            }

            // Update main trace series (index 1)
            if (Series.Count > 1 && Series[1] is PolarLineSeries<double> ls)
            {
                ls.Stroke = new SolidColorPaint(color) { StrokeThickness = GetTraceThickness() };
                ls.Fill = GetTraceFill(color);
                ls.GeometryFill = GetGeometryFill(color);
                ls.GeometrySize = GetGeometrySize();
            }
        }

        InitializeAxes();
    }

    private void RequestFlush()
    {
        if (_disposed || !_isActive || !_chartReady || _isUpdating || _numberOfChannels == 0) return;
        if (_slot is null || !_slot.HasData) return;

        if (Interlocked.Exchange(ref _flushScheduled, 1) == 1) return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            FlushToChart();
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// Reads the latest sample from the slot into the display collection.
    /// Uses lock(Sync) + BeginBatch/EndBatch to prevent LiveCharts
    /// from measuring mid-mutation.
    /// Only the latest sample matters for a spider chart (snapshot display).
    /// </summary>
    private void FlushToChart()
    {
        if (_disposed || !_isActive || !_chartReady || _isUpdating || _numberOfChannels == 0) return;
        var slot = _slot;
        if (slot is null) return;

        _isUpdating = true;
        try
        {
            if (_consumerFrame.Length != _numberOfChannels) _consumerFrame = new double[_numberOfChannels];
            if (!slot.TryReadInto(_consumerFrame)) return; // no new sample since last flush
            double[] latest = _consumerFrame;

            // Apply per-channel normalization if enabled
            double[] display = latest;
            if (_normalizePerChannel && _runningMin != null && _runningMax != null)
            {
                display = new double[latest.Length];
                for (int i = 0; i < latest.Length; i++)
                {
                    var range = _runningMax[i] - _runningMin[i];
                    display[i] = range > 1e-12
                        ? (latest[i] - _runningMin[i]) / range
                        : 0.0;
                }
            }

            lock (Sync)
            {
                if (_disposed || !_isActive) return;

                _values.BeginBatch();
                _values.Clear();
                for (int i = 0; i < _numberOfChannels && i < display.Length; i++)
                    _values.Add(display[i]);
                _values.EndBatch();

                // Update peak envelope: take max of current display and decayed peak
                if (_showPeakEnvelope && _peakValues != null && _peakValues.Length == _numberOfChannels)
                {
                    _peakCollection.BeginBatch();
                    _peakCollection.Clear();
                    for (int i = 0; i < _numberOfChannels && i < display.Length; i++)
                    {
                        // Decay existing peak, then take max with current absolute value
                        _peakValues[i] = Math.Max(Math.Abs(display[i]), _peakValues[i] * _peakDecay);
                        _peakCollection.Add(_peakValues[i]);
                    }
                    _peakCollection.EndBatch();
                }

                // Axis auto-scale uses the display values (post-normalization)
                UpdateRadiusAxisFromData(display);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SpiderMonitor] FlushToChart error: {ex.Message}");
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void UpdateRadiusAxisFromData(double[] values)
    {
        if (!_autoScale) return;  // Fixed mode — don't touch axis limits
        if (RadiusAxes.Length == 0) return;

        // In normalization mode, data is [0, 1] — keep axis fixed
        if (_normalizePerChannel)
        {
            if (double.IsNaN(_lastRadiusMax) || Math.Abs(_lastRadiusMax - 1.1) > 0.01)
            {
                var axis = RadiusAxes[0];
                axis.MinLimit = -0.1;
                axis.MaxLimit = 1.1;
                _lastRadiusMax = 1.1;
            }
            return;
        }

        double maxAbs = 0;
        for (int i = 0; i < values.Length; i++)
        {
            var abs = Math.Abs(values[i]);
            if (!double.IsNaN(abs) && !double.IsInfinity(abs) && abs > maxAbs)
                maxAbs = abs;
        }
        
        double newMax = maxAbs > 0 ? maxAbs * 1.2 : 1e-6;

        // Deadband: only update if change is significant (>25%) or data exceeds limits
        if (!double.IsNaN(_lastRadiusMax) && _lastRadiusMax > 0)
        {
            double change = Math.Abs(newMax - _lastRadiusMax) / _lastRadiusMax;
            bool dataOutside = maxAbs > _lastRadiusMax;
            bool dataTooSmall = maxAbs > 0 && maxAbs < _lastRadiusMax * 0.05;
            if (!dataOutside && change < 0.25 && !dataTooSmall) return;
        }

        var rAxis = RadiusAxes[0];
        rAxis.MinLimit = -newMax;
        rAxis.MaxLimit = newMax;
        _lastRadiusMax = newMax;
    }

    private void InitializeAxes()
    {
        var label = GetAxisLabelColor();
        var grid = GetGridColor();
        var dash = new DashEffect([3, 3]);

        var radiusAxis = new PolarAxis
        {
            Labeler = v => _normalizePerChannel ? $"{v:P0}" : FormatAxisLabel(v),
            MaxLimit = _normalizePerChannel ? 1.1 : 1,
            MinLimit = _normalizePerChannel ? -0.1 : -1,
            TextSize = GetRadiusLabelSize(),
            LabelsPaint = new SolidColorPaint(label) { IsAntialias = true },
            SeparatorsPaint = new SolidColorPaint(grid)
            {
                StrokeThickness = 1.0f,
                PathEffect = dash,
                IsAntialias = true
            },
            AnimationsSpeed = TimeSpan.Zero,
            ShowSeparatorLines = true,
        };

        string[] labels = _numberOfChannels > 0
            ? Enumerable.Range(1, _numberOfChannels).Select(i => $"Ch {i}").ToArray()
            : Array.Empty<string>();

        var angleAxis = new PolarAxis
        {
            Labels = labels,
            MinStep = 1,
            ForceStepToMin = true,
            TextSize = GetAngleLabelSize(),
            LabelsPaint = new SolidColorPaint(label) { IsAntialias = true },
            SeparatorsPaint = new SolidColorPaint(grid)
            {
                StrokeThickness = 1.0f,
                PathEffect = dash,
                IsAntialias = true
            },
            AnimationsSpeed = TimeSpan.Zero,
            ShowSeparatorLines = true,
        };

        RadiusAxes = [radiusAxis];
        AngleAxes = [angleAxis];

        _lastRadiusMax = double.NaN;
    }

    #endregion

    #region Appearance

    private SKColor GetAxisLabelColor() => ThemeHelper.ChartAxisLabel;

    private SKColor GetGridColor() => ThemeHelper.ChartGrid;

    private SKColor GetTraceColor()
    {
        var isDark = App.CurrentTheme == AppTheme.Dark;
        return isDark ? new SKColor(80, 180, 255) : new SKColor(25, 90, 210);
    }

    private SolidColorPaint? GetTraceFill(SKColor traceColor)
    {
        var isDark = App.CurrentTheme == AppTheme.Dark;
        byte alpha = isDark ? (byte)50 : (byte)80;
        return new SolidColorPaint(traceColor.WithAlpha(alpha));
    }

    /// <summary>Clamped lerp: returns <paramref name="from"/> at <paramref name="nLow"/> channels,
    /// <paramref name="to"/> at <paramref name="nHigh"/> channels, linearly interpolated between.</summary>
    private float ScaleByChannels(float from, float to, int nLow, int nHigh)
    {
        if (_numberOfChannels <= nLow) return from;
        if (_numberOfChannels >= nHigh) return to;
        float t = (float)(_numberOfChannels - nLow) / (nHigh - nLow);
        return from + (to - from) * t;
    }

    /// <summary>Main trace line thickness: 3.0 at 1 ch → 1.0 at 64 ch.</summary>
    private float GetTraceThickness() => ScaleByChannels(3.0f, 1.0f, 1, 64);

    /// <summary>Peak envelope stroke: 1.5 at 1 ch → 0.5 at 64 ch.</summary>
    private float GetPeakStrokeThickness() => ScaleByChannels(1.5f, 0.5f, 1, 64);

    /// <summary>Geometry dot size: 6.0 at 1 ch → 0 at 28 ch.</summary>
    private float GetGeometrySize() => ScaleByChannels(6.0f, 0f, 1, 28);

    /// <summary>Angle axis label font size: 14 at 1 ch → 7 at 64 ch.</summary>
    private float GetAngleLabelSize() => ScaleByChannels(14f, 7f, 1, 64);

    /// <summary>Radius axis label font size: 12 at 1 ch → 9 at 64 ch.</summary>
    private float GetRadiusLabelSize() => ScaleByChannels(12f, 9f, 1, 64);

    private SolidColorPaint? GetGeometryFill(SKColor traceColor)
    {
        // Alpha fades out continuously so dots disappear smoothly at high channel counts
        byte alpha = (byte)Math.Clamp((int)ScaleByChannels(255f, 0f, 1, 28), 0, 255);
        if (alpha == 0) return null;
        return new SolidColorPaint(traceColor.WithAlpha(alpha)) { IsAntialias = true };
    }

    private SKColor GetPeakEnvelopeColor()
    {
        var isDark = App.CurrentTheme == AppTheme.Dark;
        return isDark ? new SKColor(255, 160, 60) : new SKColor(220, 120, 20);
    }

    private SolidColorPaint? GetPeakFill(SKColor peakColor)
    {
        var isDark = App.CurrentTheme == AppTheme.Dark;
        byte alpha = isDark ? (byte)25 : (byte)35;
        return new SolidColorPaint(peakColor.WithAlpha(alpha));
    }

    private static string FormatAxisLabel(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "";

        var abs = Math.Abs(value);

        if (abs == 0) return "0";
        if (abs >= 1000) return $"{value / 1000:F1}k";
        if (abs >= 100) return $"{value:F0}";
        if (abs >= 10) return $"{value:F1}";
        if (abs >= 1) return $"{value:F2}";
        if (abs >= 0.01) return $"{value:F3}";
        return $"{value:E1}";
    }

    #endregion
}