using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;
using ScottPlot;
using ScottPlot.Plottables;
using SkiaSharp;

namespace MOSAIC.Visualization.ScopeMonitor;

public enum ScopeViewMode
{
    Overlapped,
    Stacked
}

/// <summary>
/// Real-time multi-channel scope backed by ScottPlot. Ingest, flush coalescing, channel
/// reconfigure, pause/resume and the monotonic clock live in <see cref="ChannelRingMonitorBase"/>;
/// this class supplies only the ScottPlot rendering, axis management and stacked/overlapped logic.
/// </summary>
public partial class ScopeMonitor : ChannelRingMonitorBase
{
    #region Constants & Static

    /// <summary>Palette size (also the effective max channel count).</summary>
    private const int MaxPaletteChannels = 128;

    private const int MinDataPoints = 200;
    private const int MaxDataPointsCap = 10_000;
    private const int DefaultDataPoints = 2000;

    public static readonly SKColor[] ChannelColors = GenerateColorPalette(MaxPaletteChannels);

    public static SKColor GetChannelColor(int index) => ChannelColors[index % ChannelColors.Length];

    public static string GetChannelColorHex(int index)
    {
        var c = ChannelColors[index % ChannelColors.Length];
        return $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
    }

    #endregion

    #region Fields

    private int _numberOfDataPoints = DefaultDataPoints;
    private TimeSpan _window = TimeSpan.FromSeconds(10);

    private double _signalRate;
    private bool _manualDataPoints;

    private Plot? _plot;
    private Action? _refreshCallback;
    private readonly List<DataStreamer> _streamers = new();

    private double _currentXMax;

    // --- Stable tick state ---
    private double _lastTickXMax = double.NaN;
    private double _lastTickInterval;

    // --- Grow-only Y axis state ---
    private double _growYMin = double.PositiveInfinity;
    private double _growYMax = double.NegativeInfinity;
    private double _batchYMin = double.PositiveInfinity;
    private double _batchYMax = double.NegativeInfinity;
    private int _flushCount;
    private const int YShrinkEveryNFlushes = 60;
    private const double YShrinkFactor = 0.92;
    /// <summary>Fractional Y-range change below which the axis is held steady (anti-jitter deadband).</summary>
    private const double YRescaleDeadband = 0.12; // 12%
    /// <summary>
    /// Fraction of the remaining gap the Y axis closes per flush when tightening.
    /// </summary>
    /// <remarks>
    /// Only ever applied to a shrink — growth is immediate so a transient is never clipped.
    /// At the 30 fps flush rate this settles a correction in roughly half a second, slow enough
    /// to read as a glide rather than a step and fast enough not to leave the trace small.
    /// </remarks>
    private const double YEaseFactor = 0.15;
    /// <summary>Per-frame movement, as a fraction of span, below which a tighten counts as arrived.</summary>
    private const double YEaseSettled = 0.002;
    /// <summary>True while a tighten is in progress, so it is allowed to finish. See the gate below.</summary>
    private bool _yTightening;

    // --- Auto-offset for stacked mode ---
    private bool _autoOffsetLocked;
    private double[]? _settleMin;
    private double[]? _settleMax;
    private int _settleFlushCount;
    private const int AutoOffsetSettleFlushes = 5;
    private const double AutoOffsetSpacing = 1.3;

    // Cached axis state to avoid redundant updates that cause visual blinking.
    private double _lastYMin = double.NaN;
    private double _lastYMax = double.NaN;
    private double _lastXMin = double.NaN;
    private double _lastXMax = double.NaN;

    #endregion

    #region Observable Properties

    [ObservableProperty]
    private bool _isReading = true;

    [ObservableProperty]
    private ScopeViewMode _viewMode = ScopeViewMode.Overlapped;

    [ObservableProperty]
    private double _channelOffset = 1.0;

    public bool AutoOffset { get; set; } = true;

    #endregion

    #region Configuration Properties

    /// <summary>Buffer size per channel. Setting this disables rate-based auto-scaling.</summary>
    public int MaxDataPoints
    {
        get => _numberOfDataPoints;
        set
        {
            _numberOfDataPoints = Math.Clamp(value, MinDataPoints, MaxDataPointsCap);
            _manualDataPoints = true;
        }
    }

    public TimeSpan Window
    {
        get => _window;
        set => _window = value;
    }

    /// <summary>Decimation gate for per-sample ingest (window / dataPoints, floored at 1 ms).</summary>
    private TimeSpan SampleAcceptInterval => TimeSpan.FromMilliseconds(
        Math.Max(1, _window.TotalMilliseconds / Math.Max(1, _numberOfDataPoints)));

    /// <inheritdoc/>
    protected override TimeSpan? AcceptInterval => SampleAcceptInterval;

    /// <inheritdoc/>
    protected override int MaxChannels => MaxPaletteChannels;

    /// <inheritdoc/>
    protected override bool IsRenderReady => _plot != null;

    /// <summary>True sample period for the time axis; sub-millisecond at high signal rates.</summary>
    private double SamplePeriodSec
        => _signalRate > 0
            ? 1.0 / _signalRate
            : SampleAcceptInterval.TotalSeconds;

    #endregion

    #region Signal Rate

    /// <summary>The upstream signal rate (Hz) this scope is currently sized for, or 0 if unknown.</summary>
    public double SignalRate => _signalRate;

    /// <summary>
    /// Seconds the x-axis advances per plotted row — the value handed to every streamer's
    /// <see cref="DataStreamer.Period"/>.
    /// </summary>
    /// <remarks>
    /// Exposed so the time base can be asserted directly. This is the whole correctness condition
    /// for the scope: it must equal one over the true sample rate, and it only does when some block
    /// called <see cref="UpdateSignalRate"/>. Otherwise it silently degrades to the window/buffer
    /// fallback below, which is a display constant rather than a property of the signal.
    /// </remarks>
    public double SamplePeriodSeconds => SamplePeriodSec;

    /// <summary>
    /// Sets the visible time span. Recomputes the buffer size from the current signal rate (unless
    /// <see cref="MaxDataPoints"/> was set manually) and rebuilds the streamers so the change is
    /// visible immediately. Used by the aggregate Scope Monitor window to show more history.
    /// </summary>
    public void SetWindow(TimeSpan window)
    {
        if (window.TotalSeconds <= 0) return;
        _window = window;

        int previousPoints = _numberOfDataPoints;

        if (!_manualDataPoints && _signalRate > 0)
        {
            var samplesPerWindow = _signalRate * window.TotalSeconds;
            _numberOfDataPoints = (int)Math.Clamp(samplesPerWindow, MinDataPoints, MaxDataPointsCap);
        }

        if (_plot != null && ChannelCount > 0)
        {
            // Same reasoning as UpdateSignalRate: only a changed buffer size forces a rebuild.
            // When the count is unchanged the period may still have moved (it is derived from the
            // window whenever no signal rate is known), so retarget rather than clear the trace.
            if (_numberOfDataPoints == previousPoints)
                Dispatcher.UIThread.Post(ApplyPeriodToStreamers, DispatcherPriority.Render);
            else
                Dispatcher.UIThread.Post(() => { if (!IsDisposed) RebuildStreamers(); }, DispatcherPriority.Render);
        }

        foreach (var m in Mirrors)
            if (m is ScopeMonitor s) s.SetWindow(window);
    }

    /// <summary>
    /// Informs the scope of the upstream signal rate so it can auto-scale the buffer size for
    /// smooth rendering. No-op if <see cref="MaxDataPoints"/> was set manually.
    /// </summary>
    public void UpdateSignalRate(double signalRate)
    {
        if (signalRate <= 0) return;
        if (Math.Abs(_signalRate - signalRate) < 0.1) return;

        // Keep any mirror sinks (aggregate-window scopes) sized to the same rate.
        foreach (var m in Mirrors)
            if (m is ScopeMonitor s) s.UpdateSignalRate(signalRate);

        _signalRate = signalRate;
        _manualDataPoints = false;

        var samplesPerWindow = signalRate * _window.TotalSeconds;
        var newPoints = (int)Math.Clamp(samplesPerWindow, MinDataPoints, MaxDataPointsCap);

        Debug.WriteLine($"[ScopeMonitor] Signal rate {signalRate:F0} Hz → " +
                        $"{newPoints} data points (window {_window.TotalSeconds:F1}s, " +
                        $"period {1000.0 / signalRate:F3} ms)");

        if (newPoints == _numberOfDataPoints)
        {
            // Only the time base moved, and a live DataStreamer's Period is settable — so retarget
            // the existing streamers instead of rebuilding them. A rebuild discards every buffered
            // sample and restarts the trace from an empty plot, which reads as a flicker each time
            // an upstream block re-announces its rate (SlidingWindow does so on every rate change).
            if (_plot != null && ChannelCount > 0)
                Dispatcher.UIThread.Post(ApplyPeriodToStreamers, DispatcherPriority.Render);
            return;
        }

        // The buffer size itself changed, and a DataStreamer's length is fixed at construction,
        // so this one genuinely cannot be done in place.
        _numberOfDataPoints = newPoints;

        if (_plot != null && ChannelCount > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsDisposed) return;
                RebuildStreamers();
            }, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Retargets the live streamers' time base, keeping the samples already buffered.
    /// </summary>
    /// <remarks>
    /// Used when the signal rate changes but the buffer size does not. The x-axis span the
    /// existing samples occupy changes with the period, so the cached tick labels are dropped
    /// and regenerated on the next flush.
    /// </remarks>
    private void ApplyPeriodToStreamers()
    {
        if (IsDisposed || _plot == null) return;

        double periodSec = SamplePeriodSec;
        foreach (var s in _streamers)
            s.Period = periodSec;

        _lastTickXMax = double.NaN;
        _refreshCallback?.Invoke();
    }

    #endregion

    #region Scale Mode

    private bool _yAutoScale = true;
    private double _yFixedMin = double.NaN;
    private double _yFixedMax = double.NaN;

    public bool IsAutoScale => _yAutoScale;

    public void SetAutoScale()
    {
        _yAutoScale = true;
        _yFixedMin = double.NaN;
        _yFixedMax = double.NaN;
        _growYMin = double.PositiveInfinity;
        _growYMax = double.NegativeInfinity;
        _yTightening = false;

        if (_plot != null)
        {
            _plot.Axes.Left.Min = double.NaN;
            _plot.Axes.Left.Max = double.NaN;
        }
    }

    public void SetFixedRange(double min, double max)
    {
        if (min >= max) return;
        _yAutoScale = false;
        _yFixedMin = min;
        _yFixedMax = max;

        if (_plot != null)
        {
            _plot.Axes.SetLimitsY(min, max);
            _plot.Axes.Left.Min = min;
            _plot.Axes.Left.Max = max;
            _refreshCallback?.Invoke();
        }
    }

    #endregion

    #region Plot Attachment (called by ScopeMonitorView)

    public void AttachPlot(Plot plot, Action refreshCallback)
    {
        _plot = plot;
        _refreshCallback = refreshCallback;
        ConfigurePlotAppearance();
        RebuildStreamers();
    }

    public void DetachPlot()
    {
        _plot = null;
        _refreshCallback = null;
        _streamers.Clear();
    }

    #endregion

    #region Init / Reset

    public void ScopeInit(int count = 0) => ConfigureChannels(count);

    /// <summary>No-op — kept for API compatibility; still invoked by VisualizationPanel.</summary>
    public void NotifyChartReady() { }

    #endregion

    #region View Mode

    private volatile bool _suppressViewModeHandler;

    public void SetViewModeSilent(ScopeViewMode mode)
    {
        _suppressViewModeHandler = true;
        ViewMode = mode;
        _suppressViewModeHandler = false;
    }

    public void ToggleViewMode()
    {
        ViewMode = ViewMode == ScopeViewMode.Overlapped ? ScopeViewMode.Stacked : ScopeViewMode.Overlapped;
    }

    partial void OnViewModeChanged(ScopeViewMode value)
    {
        if (_suppressViewModeHandler) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (IsDisposed || IsUpdating) return;

            IsUpdating = true;
            try
            {
                if (value == ScopeViewMode.Stacked && AutoOffset)
                    ResetAutoOffset();

                RebuildStreamers();
            }
            finally { IsUpdating = false; }
        }, DispatcherPriority.Render);
    }

    partial void OnChannelOffsetChanged(double value)
    {
        // No rebuild — new data arrives with the updated offset and old data scrolls off naturally.
    }

    #endregion

    #region Render hooks (from ChannelRingMonitorBase)

    /// <inheritdoc/>
    protected override void OnFlush(SampleRingBuffer ring, int n)
    {
        if (_plot == null) return;

        if (ViewMode == ScopeViewMode.Stacked && AutoOffset)
            UpdateAutoOffset(ring, n);

        double baseOffset = (ViewMode == ScopeViewMode.Stacked) ? ChannelOffset : 0.0;

        double batchMin = double.PositiveInfinity;
        double batchMax = double.NegativeInfinity;

        for (int ch = 0; ch < ChannelCount && ch < _streamers.Count; ch++)
        {
            var streamer = _streamers[ch];
            double off = (ViewMode == ScopeViewMode.Stacked) ? ch * baseOffset : 0.0;

            for (int f = 0; f < n; f++)
            {
                double v = ring.Peek(f, ch);
                if (double.IsNaN(v) || double.IsInfinity(v)) v = 0.0;
                double stored = v + off;
                streamer.Add(stored);
                if (stored < batchMin) batchMin = stored;
                if (stored > batchMax) batchMax = stored;
            }

            streamer.ViewScrollLeft();
        }

        _batchYMin = batchMin;
        _batchYMax = batchMax;

        if (_streamers.Count > 0)
        {
            var limits = _streamers[0].GetAxisLimits();
            _currentXMax = limits.Right;
        }

        UpdateYAxisFromData();
        UpdateTimeAxisTicksStable();

        _refreshCallback?.Invoke();
        _flushCount++;
    }

    /// <inheritdoc/>
    protected override void OnRebuild() => RebuildStreamers();

    /// <inheritdoc/>
    protected override void OnResetData()
    {
        _growYMin = double.PositiveInfinity;
        _growYMax = double.NegativeInfinity;
        _yTightening = false;
        _lastTickXMax = double.NaN;
        _autoOffsetLocked = false;
        _settleMin = null;
        _settleMax = null;
        _settleFlushCount = 0;
        _flushCount = 0;
        RebuildStreamers();
    }

    /// <inheritdoc/>
    protected override void OnResumed()
    {
        _growYMin = double.PositiveInfinity;
        _growYMax = double.NegativeInfinity;
        _yTightening = false;
        _lastTickXMax = double.NaN;
        _autoOffsetLocked = false;
        _settleMin = null;
        _settleMax = null;
        _settleFlushCount = 0;
        _flushCount = 0;
    }

    /// <inheritdoc/>
    protected override void OnDispose()
    {
        _plot = null;
        _refreshCallback = null;
        _streamers.Clear();
    }

    #endregion

    #region Plot Configuration

    private void RebuildStreamers()
    {
        if (_plot == null) return;

        foreach (var s in _streamers)
            _plot.Remove(s);
        _streamers.Clear();

        _lastYMin = double.NaN;
        _lastYMax = double.NaN;
        _lastXMin = double.NaN;
        _lastXMax = double.NaN;

        double periodSec = SamplePeriodSec;

        float thickness = ChannelCount > 32 ? 1.0f : ChannelCount > 16 ? 1.5f : 2.0f;

        for (int ch = 0; ch < ChannelCount; ch++)
        {
            var streamer = _plot.Add.DataStreamer(_numberOfDataPoints);
            streamer.Period = periodSec;
            streamer.ManageAxisLimits = false;
            var skColor = ChannelColors[ch % ChannelColors.Length];
            streamer.Color = new ScottPlot.Color(skColor.Red, skColor.Green, skColor.Blue);
            streamer.LineWidth = thickness;
            streamer.ViewScrollLeft();
            _streamers.Add(streamer);
        }

        _currentXMax = 0;
        _lastTickXMax = double.NaN;
        _growYMin = double.PositiveInfinity;
        _growYMax = double.NegativeInfinity;
        _yTightening = false;
        _autoOffsetLocked = false;
        _settleMin = null;
        _settleMax = null;
        _settleFlushCount = 0;
        _refreshCallback?.Invoke();
    }

    private void ConfigurePlotAppearance()
    {
        if (_plot == null) return;

        var isDark = App.CurrentTheme == AppTheme.Dark;

        var bgColor = isDark
            ? ScottPlot.Color.FromHex("#0A0C0F")
            : ScottPlot.Color.FromHex("#FFFFFF");
        _plot.FigureBackground.Color = bgColor;
        _plot.DataBackground.Color = bgColor;

        var gridColor = isDark
            ? ScottPlot.Color.FromHex("#FFFFFF14")
            : ScottPlot.Color.FromHex("#6B728264");
        _plot.Grid.MajorLineColor = gridColor;
        _plot.Grid.MinorLineColor = gridColor.WithAlpha(80);

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

        _plot.Axes.Bottom.TickLabelStyle.FontSize = 11;
        _plot.Axes.Left.TickLabelStyle.FontSize = 11;

        _plot.Axes.Bottom.Label.Text = "";
        _plot.Axes.Left.Label.Text = "";

        _plot.HideLegend();
    }

    /// <inheritdoc/>
    public override void UpdateThemeColors()
    {
        if (IsDisposed || _plot == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            ConfigurePlotAppearance();
            _refreshCallback?.Invoke();
        }, DispatcherPriority.Render);
    }

    private void UpdateYAxisFromData()
    {
        if (_plot == null) return;

        if (_streamers.Count > 0)
        {
            var xLimits = _streamers[0].GetAxisLimits();
            if (xLimits.Left != _lastXMin || xLimits.Right != _lastXMax)
            {
                _plot.Axes.SetLimitsX(xLimits.Left, xLimits.Right);
                _lastXMin = xLimits.Left;
                _lastXMax = xLimits.Right;
            }
        }

        double newYMin, newYMax;

        if (!_yAutoScale)
        {
            if (double.IsNaN(_yFixedMin) || double.IsNaN(_yFixedMax)) return;
            newYMin = _yFixedMin;
            newYMax = _yFixedMax;
        }
        else
        {
            if (_batchYMin < _growYMin) _growYMin = _batchYMin;
            if (_batchYMax > _growYMax) _growYMax = _batchYMax;

            if (_flushCount % YShrinkEveryNFlushes == 0 && _flushCount > 0)
            {
                double dataMin = double.PositiveInfinity;
                double dataMax = double.NegativeInfinity;

                foreach (var streamer in _streamers)
                {
                    var data = streamer.Data;
                    if (data.CountTotal == 0) continue;

                    int count = Math.Min(data.CountTotal, data.Length);
                    for (int i = 0; i < count; i++)
                    {
                        var v = data.Data[i];
                        if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                        if (v < dataMin) dataMin = v;
                        if (v > dataMax) dataMax = v;
                    }
                }

                if (!double.IsInfinity(dataMin) && !double.IsInfinity(dataMax))
                {
                    _growYMin = _growYMin + (1.0 - YShrinkFactor) * (dataMin - _growYMin);
                    _growYMax = _growYMax + (1.0 - YShrinkFactor) * (dataMax - _growYMax);
                }
            }

            if (double.IsInfinity(_growYMin) || double.IsInfinity(_growYMax))
            {
                newYMin = -1;
                newYMax = 1;
            }
            else
            {
                if (ViewMode == ScopeViewMode.Stacked)
                {
                    double range = _growYMax - _growYMin;
                    double padding = range > 0 ? range * 0.2 : 0.5;
                    newYMin = _growYMin - padding;
                    newYMax = _growYMax + padding;
                }
                else
                {
                    const double padPct = 0.4;
                    var maxAbs = Math.Max(Math.Abs(_growYMin), Math.Abs(_growYMax));
                    var halfSpan = maxAbs > 0 ? maxAbs * (1.0 + padPct) : 0.1;
                    newYMin = -halfSpan;
                    newYMax = halfSpan;
                }
            }
        }

        // Hysteresis: hold the current Y limits unless the data pokes outside them (expand) or the
        // axis is now much larger than needed (tighten). Without this, tiny per-flush peak changes
        // rescale the whole view every frame — hidden in STACKED mode (channel offsets dominate the
        // range) but a visible vertical "jitter" in OVERLAPPED mode, where the axis is the peak.
        if (_yAutoScale && !double.IsNaN(_lastYMin) && !double.IsNaN(_lastYMax))
        {
            bool dataClips  = _growYMax > _lastYMax || _growYMin < _lastYMin;
            double lastSpan = _lastYMax - _lastYMin;
            double newSpan  = newYMax - newYMin;
            bool axisTooBig = lastSpan > 0 && (lastSpan - newSpan) / lastSpan > YRescaleDeadband;

            // The deadband is there to stop a rescale STARTING on noise, not to stop one finishing.
            // Re-testing it against every eased step would end the glide as soon as the remaining
            // gap fell inside it, parking the axis a deadband short of the target for good — so a
            // tighten latches once it begins and is released when it arrives.
            if (dataClips) _yTightening = false;
            else if (axisTooBig) _yTightening = true;

            if (!dataClips && !_yTightening) return; // inside the deadband → hold, no jitter

            // Fast attack, slow release. Expanding happens at once, because anything slower clips a
            // transient off the top of the view for as long as it takes to catch up. Tightening is
            // only cosmetic, so it eases instead: by the time the deadband is exceeded the
            // correction is at least 12% of the span, and applying that in one frame is exactly the
            // vertical jump this is meant to avoid.
            if (!dataClips)
            {
                double easedMin = _lastYMin + (newYMin - _lastYMin) * YEaseFactor;
                double easedMax = _lastYMax + (newYMax - _lastYMax) * YEaseFactor;

                double moved = Math.Abs(easedMin - _lastYMin) + Math.Abs(easedMax - _lastYMax);
                if (moved < Math.Max(1e-12, lastSpan) * YEaseSettled) _yTightening = false;

                newYMin = easedMin;
                newYMax = easedMax;
            }
        }

        if (newYMin == _lastYMin && newYMax == _lastYMax) return;

        _plot.Axes.SetLimitsY(newYMin, newYMax);
        if (!_yAutoScale)
        {
            _plot.Axes.Left.Min = newYMin;
            _plot.Axes.Left.Max = newYMax;
        }
        _lastYMin = newYMin;
        _lastYMax = newYMax;
    }

    private void UpdateTimeAxisTicksStable()
    {
        if (_plot == null || _currentXMax <= 0) return;

        var windowSec = _numberOfDataPoints * SamplePeriodSec;
        if (!(windowSec > 0)) windowSec = _window.TotalSeconds;

        double interval;
        if (windowSec <= 20) interval = 1.0;
        else if (windowSec <= 60) interval = 2.0;
        else interval = 5.0;

        if (!double.IsNaN(_lastTickXMax) &&
            _lastTickInterval == interval &&
            Math.Abs(_currentXMax - _lastTickXMax) < interval * 0.5)
        {
            return;
        }

        _lastTickXMax = _currentXMax;
        _lastTickInterval = interval;

        double snappedXMax = Math.Round(_currentXMax / interval) * interval;
        var xMin = _currentXMax - windowSec;

        var tickGen = new ScottPlot.TickGenerators.NumericManual();

        for (double x = snappedXMax; x >= xMin; x -= interval)
        {
            var secsAgo = _currentXMax - x;
            if (secsAgo < 0) continue;

            string label = secsAgo < 0.5 ? "now" : $"{secsAgo:N0}s";
            tickGen.AddMajor(x, label);
        }

        _plot.Axes.Bottom.TickGenerator = tickGen;
    }

    #endregion

    #region Offset Helpers

    private void ResetAutoOffset()
    {
        _autoOffsetLocked = false;
        _settleMin = null;
        _settleMax = null;
        _settleFlushCount = 0;
    }

    public void ResetAutoOffsetPublic() => ResetAutoOffset();

    private void UpdateAutoOffset(SampleRingBuffer ring, int n)
    {
        if (_autoOffsetLocked) return;
        if (n == 0 || ChannelCount == 0) return;

        if (_settleMin == null || _settleMax == null)
        {
            _settleMin = new double[ChannelCount];
            _settleMax = new double[ChannelCount];
            for (int i = 0; i < ChannelCount; i++)
            {
                _settleMin[i] = double.MaxValue;
                _settleMax[i] = double.MinValue;
            }
            _settleFlushCount = 0;
        }

        for (int ch = 0; ch < ChannelCount && ch < _settleMin.Length; ch++)
        {
            for (int f = 0; f < n; f++)
            {
                var v = ring.Peek(f, ch);
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                if (v < _settleMin[ch]) _settleMin[ch] = v;
                if (v > _settleMax[ch]) _settleMax[ch] = v;
            }
        }

        _settleFlushCount++;

        double maxAmp = 0;
        for (int ch = 0; ch < ChannelCount && ch < _settleMin.Length; ch++)
        {
            if (_settleMin[ch] < double.MaxValue && _settleMax[ch] > double.MinValue)
            {
                var amp = _settleMax[ch] - _settleMin[ch];
                if (amp > maxAmp) maxAmp = amp;
            }
        }

        if (maxAmp <= 0) maxAmp = 1e-6;

        ChannelOffset = maxAmp * AutoOffsetSpacing;

        if (_settleFlushCount >= AutoOffsetSettleFlushes)
        {
            _autoOffsetLocked = true;
            _settleMin = null;
            _settleMax = null;
        }
    }

    #endregion

    #region Color Palette

    private static SKColor[] GenerateColorPalette(int count)
    {
        var colors = new List<SKColor>();

        var tier1 = new SKColor[]
        {
            new(255, 80, 80),   new(80, 255, 120),  new(80, 180, 255),
            new(255, 255, 80),  new(255, 80, 255),   new(80, 255, 255),
            new(255, 180, 80),  new(180, 100, 255), new(255, 140, 200),
            new(140, 255, 180), new(200, 220, 255), new(255, 220, 140),
            new(220, 160, 255), new(140, 255, 220), new(255, 120, 80),
            new(180, 255, 100),
        };
        colors.AddRange(tier1);

        foreach (var c in tier1)
            colors.Add(new SKColor(
                (byte)Math.Min(255, c.Red + 50),
                (byte)Math.Min(255, c.Green + 50),
                (byte)Math.Min(255, c.Blue + 50)));

        foreach (var c in tier1)
            colors.Add(new SKColor(
                (byte)(c.Red * 0.6),
                (byte)(c.Green * 0.6),
                (byte)(c.Blue * 0.6)));

        foreach (var c in tier1)
            colors.Add(new SKColor(
                (byte)((c.Red + c.Blue) / 2),
                (byte)((c.Green + c.Red) / 2),
                (byte)((c.Blue + c.Green) / 2)));

        for (int i = 0; colors.Count < count; i++)
        {
            float hue = ((i * 137.508f) + 20f) % 360f;
            float saturation = 0.8f + (i % 3) * 0.07f;
            float lightness = 0.5f + (i % 4) * 0.08f;
            colors.Add(HslToSkColor(hue, saturation, lightness));
        }

        return colors.ToArray();
    }

    private static SKColor HslToSkColor(float h, float s, float l)
    {
        float c = (1 - Math.Abs(2 * l - 1)) * s;
        float x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
        float m = l - c / 2;

        float r, g, b;
        if (h < 60)       { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else               { r = c; g = 0; b = x; }

        return new SKColor(
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }

    #endregion
}