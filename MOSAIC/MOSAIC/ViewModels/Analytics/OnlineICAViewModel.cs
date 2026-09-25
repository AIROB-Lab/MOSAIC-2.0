using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.Analytics;
using MOSAIC.Services;
using MOSAIC.Visualization.Scatter3D;
using SkiaSharp;

namespace MOSAIC.ViewModels.Analytics;

/// <summary>
/// ViewModel for the Online ICA visualisation card.
/// </summary>
/// <remarks>
/// <para>
/// Bridges the <see cref="OnlineICA"/> model and the Avalonia UI. It subscribes to the model
/// via <see cref="ISubscriber"/> and receives projected <c>Vector&lt;double&gt;</c> values on a
/// background thread. Incoming points are buffered in <see cref="_pointBuffer"/> and consumed on
/// the shared <see cref="VisualizationTimer"/> tick so that all chart mutations happen on the UI thread.
/// </para>
/// <para>
/// <b>Scatter chart:</b> A single <see cref="ScatterSeries{TModel}"/> backed by
/// <see cref="_scatterPoints"/> plots IC1 on the X-axis and IC2 on the Y-axis.
/// Older points are evicted from the front of the collection once <see cref="MaxScatterPoints"/>
/// is reached, keeping memory bounded.
/// </para>
/// <para>
/// <b>Axis scaling:</b> Bounds are tracked under <see cref="_lock"/> as points arrive.
/// On each timer tick <see cref="UpdateAxisBoundsUI"/> expands the visible range only when
/// a data point falls outside the current limits, using a square aspect ratio with a 30 % margin.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="_pointBuffer"/> and the axis-bound tracking fields are
/// guarded by <see cref="_lock"/>. All LiveCharts mutations happen exclusively on the UI thread
/// via the shared timer callback.
/// </para>
/// </remarks>
public partial class OnlineICAViewModel : ObservableObject, ISubscriber, IDisposable
{
    #region Fields

    /// <summary>The underlying Online ICA model this ViewModel wraps.</summary>
    private readonly OnlineICA _onlineIca;

    /// <summary>Guards <see cref="_pointBuffer"/> and the axis-bound tracking fields.</summary>
    private readonly object _lock = new();

    /// <summary>Cached delegate registered with <see cref="VisualizationTimer"/> to avoid allocations.</summary>
    private readonly Action _timerCallback;

    /// <summary>Set to <see langword="true"/> by <see cref="Dispose"/> to stop timer and subscriber callbacks.</summary>
    private bool _disposed;

    #endregion

    #region Point Buffer

    /// <summary>
    /// Staging buffer that collects incoming <c>(x, y)</c> tuples from the background thread.
    /// Drained on every <see cref="VisualizationTimer"/> tick on the UI thread.
    /// </summary>
    private readonly List<(double X, double Y)> _pointBuffer = new();

    #endregion

    #region Scatter Points

    /// <summary>
    /// Live point collection bound directly to the scatter series.
    /// Only ever mutated on the UI thread inside <see cref="ProcessPointBuffer"/>.
    /// </summary>
    private readonly ObservableCollection<ObservablePoint> _scatterPoints = new();

    #endregion

    #region Observable Properties

    /// <summary>
    /// Maximum number of scatter points retained before the oldest are evicted.
    /// Changing this value also marks the axis bounds as dirty so the chart re-fits.
    /// </summary>
    [ObservableProperty]
    private int _maxScatterPoints = 1000;

    /// <summary>Array of series bound to the main scatter chart.</summary>
    [ObservableProperty] 
    private ISeries[] _scatterSeries = Array.Empty<ISeries>();

    /// <summary>X-axis configuration for the scatter chart (IC1).</summary>
    [ObservableProperty] 
    private Axis[] _xAxes = Array.Empty<Axis>();

    /// <summary>Y-axis configuration for the scatter chart (IC2).</summary>
    [ObservableProperty]
    private Axis[] _yAxes = Array.Empty<Axis>();

    /// <summary>Header displayed above the scatter chart, e.g. <c>"IC1 vs IC2"</c>.</summary>
    [ObservableProperty] 
    private string _chartTitle = "";

    /// <summary>Human-readable total sample count, e.g. <c>"1 024 samples"</c>.</summary>
    [ObservableProperty] 
    private string _sampleCountText = "0 samples";

    /// <summary>
    /// <see langword="true"/> when the underlying model operates in 3-component mode,
    /// enabling the <see cref="Scatter3D"/> monitor and the IC3 kurtosis indicator.
    /// </summary>
    [ObservableProperty]
    private bool _is3D;

    /// <summary>
    /// When <see langword="true"/>, the 3D projection pane is shown (only meaningful in 3-component
    /// mode). Toggled from the view; a guard keeps at least one of the 3D/2D panes visible.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Show3DPane))]
    private bool _show3DView = true;

    /// <summary>When <see langword="true"/>, the 2D (IC1 vs IC2) scatter pane is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Show2DPane))]
    private bool _show2DView = true;

    /// <summary>Excess kurtosis of the first independent component (IC1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxAbsKurtosis))]
    private double _ic1Kurtosis;

    /// <summary>Excess kurtosis of the second independent component (IC2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxAbsKurtosis))]
    private double _ic2Kurtosis;

    /// <summary>
    /// Excess kurtosis of the third independent component (IC3).
    /// Only meaningful when <see cref="Is3D"/> is <see langword="true"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxAbsKurtosis))]
    private double _ic3Kurtosis;

    /// <summary>
    /// Largest absolute kurtosis magnitude across the active components, used to scale the
    /// per-component metric bars. Includes IC3 only when <see cref="Is3D"/> is <see langword="true"/>.
    /// </summary>
    public double MaxAbsKurtosis
    {
        get
        {
            var m = System.Math.Max(System.Math.Abs(Ic1Kurtosis), System.Math.Abs(Ic2Kurtosis));
            if (Is3D) m = System.Math.Max(m, System.Math.Abs(Ic3Kurtosis));
            return m;
        }
    }

    #endregion

    #region Axis Bounds

    /// <summary>Running minimum X value across all received projections; used for dynamic axis scaling.</summary>
    private double _minX = -5;

    /// <summary>Running maximum X value across all received projections; used for dynamic axis scaling.</summary>
    private double _maxX = 5;

    /// <summary>Running minimum Y value across all received projections; used for dynamic axis scaling.</summary>
    private double _minY = -5;

    /// <summary>Running maximum Y value across all received projections; used for dynamic axis scaling.</summary>
    private double _maxY = 5;

    /// <summary>Cached lower bound of the last applied square axis range; used for diagnostic purposes.</summary>
    private double _currentAxisMin = -5;

    /// <summary>Cached upper bound of the last applied square axis range; used for diagnostic purposes.</summary>
    private double _currentAxisMax = 5;

    /// <summary>
    /// Set to <see langword="true"/> when at least one bound has changed since the last
    /// <see cref="UpdateAxisBoundsUI"/> call. Reset after the update is applied.
    /// </summary>
    private bool _axisBoundsDirty;

    /// <summary>Fractional margin added to each side of the data range when computing axis limits (30 %).</summary>
    private const double AxisMarginFactor = 0.30;

    #endregion

    #region Pending-Update Flags

    /// <summary>Timestamp of the last throttled text-property update (sample count, kurtosis values).</summary>
    private DateTime _lastTextUpdate = DateTime.MinValue;

    #endregion

    #region Public Surface

    /// <summary>Exposes the underlying <see cref="OnlineICA"/> model for direct binding from the view.</summary>
    public OnlineICA OnlineICA => _onlineIca;

    /// <summary>
    /// <see langword="true"/> when the 3D projection pane should render: 3-component mode and the
    /// user has not hidden it via the view toggle.
    /// </summary>
    public bool Show3DPane => Is3D && Show3DView;

    /// <summary>
    /// <see langword="true"/> when the 2D scatter pane should render: always in 2-component mode,
    /// otherwise whenever the user has not hidden it.
    /// </summary>
    public bool Show2DPane => !Is3D || Show2DView;

    #endregion

    #region 3D Scatter

    /// <summary>
    /// Optional 3D scatter monitor. Created only when the model runs in 3-component mode;
    /// <see langword="null"/> when <see cref="Is3D"/> is <see langword="false"/>.
    /// </summary>
    public Scatter3DMonitor? Scatter3D { get; }

    #endregion

    #region Constructor

    /// <summary>
    /// Initialises the ViewModel, wires up model subscriptions, and registers with the shared
    /// visualisation timer.
    /// </summary>
    /// <param name="onlineIca">The <see cref="OnlineICA"/> model to visualise. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="onlineIca"/> is <see langword="null"/>.</exception>
    public OnlineICAViewModel(OnlineICA onlineIca)
    {
        _onlineIca = onlineIca ?? throw new ArgumentNullException(nameof(onlineIca));
        _is3D = onlineIca.ComponentCount == 3;
        _chartTitle = _is3D ? "IC1 vs IC2 vs IC3" : "IC1 vs IC2";

        _timerCallback = OnTimerTick;

        if (_is3D)
        {
            Scatter3D = new Scatter3DMonitor(maxPoints: _maxScatterPoints)
            {
                XLabel = "IC1",
                YLabel = "IC2",
                ZLabel = "IC3",
            };
        }

        InitializeCharts();

        _onlineIca.AddSubscriber(this);

        VisualizationTimer.Instance.Subscribe(_timerCallback);
    }

    #endregion

    #region Property Callbacks

    /// <summary>
    /// Syncs <see cref="Scatter3DMonitor.MaxPoints"/> and marks axis bounds as dirty when
    /// <see cref="MaxScatterPoints"/> changes so the chart re-fits on the next timer tick.
    /// </summary>
    partial void OnMaxScatterPointsChanged(int value)
    {
        if (Scatter3D != null)
            Scatter3D.MaxPoints = value;

        _axisBoundsDirty = true;
    }

    /// <summary>
    /// Keeps at least one projection pane visible when the user toggles 3D off, and stops the
    /// 3D renderer while the pane is hidden so it does not keep drawing frames nobody can see.
    /// </summary>
    partial void OnShow3DViewChanged(bool value)
    {
        if (!value && !Show2DView) Show2DView = true;

        if (value) Scatter3D?.Resume(); else Scatter3D?.Pause();
    }

    /// <summary>Keeps at least one projection pane visible when the user toggles 2D off.</summary>
    partial void OnShow2DViewChanged(bool value)
    {
        if (!value && !Show3DView) Show3DView = true;
    }

    #endregion

    #region Initialisation

    /// <summary>
    /// Creates the default axis and series objects for the scatter chart.
    /// Called once from the constructor.
    /// </summary>
    private void InitializeCharts()
    {
        ScatterSeries = new ISeries[]
        {
            new ScatterSeries<ObservablePoint>
            {
                Values = _scatterPoints,
                GeometrySize = 8,
                Fill = new SolidColorPaint(new SKColor(0, 191, 255, 200)),
                Stroke = new SolidColorPaint(new SKColor(0, 255, 136), 1),
                Name = "Projections"
            }
        };
        XAxes = new Axis[]
        {
            new Axis
            {
                Name = "IC1",
                NameTextSize = 12,
                NamePaint = new SolidColorPaint(SKColors.WhiteSmoke),
                LabelsPaint = new SolidColorPaint(SKColors.Gray),
                SeparatorsPaint = new SolidColorPaint(new SKColor(64, 64, 64)),
                MinLimit = -5,
                MaxLimit = 5
            }
        };
        YAxes = new Axis[]
        {
            new Axis
            {
                Name = "IC2",
                NameTextSize = 12,
                NamePaint = new SolidColorPaint(SKColors.WhiteSmoke),
                LabelsPaint = new SolidColorPaint(SKColors.Gray),
                SeparatorsPaint = new SolidColorPaint(new SKColor(64, 64, 64)),
                MinLimit = -5,
                MaxLimit = 5
            }
        };
    }

    #endregion

    #region Timer Callback

    /// <summary>
    /// Called on the UI thread by <see cref="VisualizationTimer"/> at its configured interval.
    /// Drains <see cref="_pointBuffer"/> and refreshes axis bounds.
    /// </summary>
    private void OnTimerTick()
    {
        if (_disposed) return;

        try
        {
            ProcessPointBuffer();
            UpdateAxisBoundsUI();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OnlineICAViewModel] Timer error: {ex.Message}");
        }
    }

    #endregion

    #region Point Processing

    /// <summary>
    /// Drains <see cref="_pointBuffer"/> and appends the batched points to
    /// <see cref="_scatterPoints"/>, evicting the oldest entries from the front of the
    /// collection when <see cref="MaxScatterPoints"/> is exceeded.
    /// </summary>
    private void ProcessPointBuffer()
    {
        List<(double X, double Y)> points;

        lock (_lock)
        {
            if (_pointBuffer.Count == 0) return;
            points = new List<(double, double)>(_pointBuffer);
            _pointBuffer.Clear();
        }

        while (_scatterPoints.Count + points.Count > MaxScatterPoints && _scatterPoints.Count > 0)
        {
            _scatterPoints.RemoveAt(0);
        }

        foreach (var (x, y) in points)
        {
            _scatterPoints.Add(new ObservablePoint(x, y));
        }
    }

    #endregion

    #region ISubscriber

    /// <summary>
    /// Receives a projected vector published by <see cref="OnlineICA"/>.
    /// </summary>
    /// <param name="sender">The publishing block (ignored).</param>
    /// <param name="value">
    /// Expected to be a <c>Vector&lt;double&gt;</c> containing at least two components (IC1, IC2).
    /// </param>
    /// <remarks>
    /// Called on a background thread. The IC1/IC2 coordinates are buffered in
    /// <see cref="_pointBuffer"/> under <see cref="_lock"/> and axis bounds are updated
    /// immediately. Sample count and kurtosis values are dispatched to the UI thread at most
    /// once per 100 ms.
    /// </remarks>
    public void ReceiveInput(object sender, object value)
    {
        if (_disposed) return;
        if (value is not MathNet.Numerics.LinearAlgebra.Vector<double> projection) return;
        if (projection.Count < 2) return;

        double x = projection[0];
        double y = projection[1];

        if (Is3D && projection.Count >= 3)
        {
            Scatter3D?.AddPoint(x, y, projection[2]);
        }

        lock (_lock)
        {
            _pointBuffer.Add((x, y));

            bool boundsChanged = false;
            if (x < _minX) { _minX = x; boundsChanged = true; }
            if (x > _maxX) { _maxX = x; boundsChanged = true; }
            if (y < _minY) { _minY = y; boundsChanged = true; }
            if (y > _maxY) { _maxY = y; boundsChanged = true; }

            if (boundsChanged) _axisBoundsDirty = true;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastTextUpdate).TotalMilliseconds >= 100)
        {
            _lastTextUpdate = now;
            var sampleCount = _onlineIca.SampleCount;
            var kurtosis = _onlineIca.Kurtosis;

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                SampleCountText = $"{sampleCount} samples";

                if (kurtosis?.Count >= 2)
                {
                    Ic1Kurtosis = kurtosis[0];
                    Ic2Kurtosis = kurtosis[1];
                    if (Is3D && kurtosis.Count >= 3)
                        Ic3Kurtosis = kurtosis[2];
                }
            }, DispatcherPriority.Background);
        }
    }

    #endregion

    #region Axis Helpers

    /// <summary>
    /// Expands the scatter chart axes when any data point falls outside the current visible range.
    /// Uses a square aspect ratio with a 30 % margin around the full data extent.
    /// No-ops when all points are already within the current limits.
    /// </summary>
    private void UpdateAxisBoundsUI()
    {
        double minX, maxX, minY, maxY;

        lock (_lock)
        {
            minX = _minX;
            maxX = _maxX;
            minY = _minY;
            maxY = _maxY;
        }

        double currentXMin = XAxes?[0]?.MinLimit ?? -5;
        double currentXMax = XAxes?[0]?.MaxLimit ?? 5;
        double currentYMin = YAxes?[0]?.MinLimit ?? -5;
        double currentYMax = YAxes?[0]?.MaxLimit ?? 5;

        bool outOfBounds = minX < currentXMin || maxX > currentXMax ||
                           minY < currentYMin || maxY > currentYMax;

        if (!outOfBounds) return;

        double rangeX = maxX - minX;
        double rangeY = maxY - minY;
        if (rangeX < 0.1) rangeX = 1.0;
        if (rangeY < 0.1) rangeY = 1.0;

        double maxRange = Math.Max(rangeX, rangeY);
        double margin = maxRange * AxisMarginFactor;
        double totalRange = maxRange + 2 * margin;

        double centerX = (minX + maxX) / 2;
        double centerY = (minY + maxY) / 2;

        double halfRange = totalRange / 2;
        double newXMin = centerX - halfRange;
        double newXMax = centerX + halfRange;
        double newYMin = centerY - halfRange;
        double newYMax = centerY + halfRange;

        _currentAxisMin = Math.Min(newXMin, newYMin);
        _currentAxisMax = Math.Max(newXMax, newYMax);

        if (XAxes?.Length > 0)
        {
            XAxes[0].MinLimit = newXMin;
            XAxes[0].MaxLimit = newXMax;
        }

        if (YAxes?.Length > 0)
        {
            YAxes[0].MinLimit = newYMin;
            YAxes[0].MaxLimit = newYMax;
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Unsubscribes from the <see cref="VisualizationTimer"/> and the <see cref="OnlineICA"/> model,
    /// and disposes the <see cref="Scatter3DMonitor"/> if one was created, preventing further timer
    /// ticks or subscriber callbacks after the card is closed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        VisualizationTimer.Instance.Unsubscribe(_timerCallback);
        _onlineIca?.RemoveSubscriber(this);
        Scatter3D?.Dispose();
    }

    #endregion
}
