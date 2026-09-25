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
/// ViewModel for the Online PCA visualisation card.
/// </summary>
/// <remarks>
/// <para>
/// Bridges the <see cref="OnlinePCA"/> model and the Avalonia UI. It subscribes to the model
/// via <see cref="ISubscriber"/> and receives projected <c>Vector&lt;double&gt;</c> values on a
/// background thread. Incoming points are buffered in <see cref="_pointBuffer"/> and consumed on
/// the shared <see cref="VisualizationTimer"/> tick so that all chart mutations happen on the UI thread.
/// </para>
/// <para>
/// <b>Scatter chart:</b> A single <see cref="ScatterSeries{TModel}"/> backed by
/// <see cref="_scatterPoints"/> plots PC1 on the X-axis and PC2 on the Y-axis.
/// Older points are evicted from the front of the collection once <see cref="MaxScatterPoints"/>
/// is reached, keeping memory bounded.
/// </para>
/// <para>
/// <b>3D scatter:</b> When the model runs in 3-component mode (<see cref="Is3D"/> is
/// <see langword="true"/>), a <see cref="Scatter3DMonitor"/> is created at construction time and
/// receives all three principal-component coordinates on every tick via
/// <see cref="Scatter3DMonitor.AddPoint"/>. The 2D LiveCharts scatter continues to receive
/// PC1/PC2 regardless.
/// </para>
/// <para>
/// <b>Axis scaling:</b> Bounds are tracked under <see cref="_lock"/> as points arrive and
/// <see cref="_axisBoundsDirty"/> is set. On the next timer tick <see cref="UpdateAxisBoundsUI"/>
/// re-fits both axes using a square aspect ratio with a 50 % margin.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="_pointBuffer"/> and the axis-bound tracking fields are
/// guarded by <see cref="_lock"/>. All LiveCharts mutations happen exclusively on the UI thread
/// via the shared timer callback.
/// </para>
/// </remarks>
public partial class OnlinePCAViewModel : ObservableObject, ISubscriber, IDisposable
{
    #region Fields

    /// <summary>The underlying Online PCA model this ViewModel wraps.</summary>
    private readonly OnlinePCA _onlinePca;

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
    /// Live point collection bound directly to the 2D scatter series.
    /// Only ever mutated on the UI thread inside <see cref="ProcessPointBuffer"/>.
    /// </summary>
    private readonly ObservableCollection<ObservablePoint> _scatterPoints = new();

    #endregion

    #region Observable Properties

    /// <summary>
    /// Maximum number of scatter points retained before the oldest are evicted.
    /// Changing this value also syncs the <see cref="Scatter3DMonitor.MaxPoints"/> limit and
    /// marks the axis bounds as dirty so the chart re-fits.
    /// </summary>
    [ObservableProperty]
    private int _maxScatterPoints = 1000;

    /// <summary>Array of series bound to the main 2D scatter chart.</summary>
    [ObservableProperty] private ISeries[] _scatterSeries = Array.Empty<ISeries>();

    /// <summary>X-axis configuration for the scatter chart (PC1).</summary>
    [ObservableProperty] private Axis[] _xAxes = Array.Empty<Axis>();

    /// <summary>Y-axis configuration for the scatter chart (PC2).</summary>
    [ObservableProperty] private Axis[] _yAxes = Array.Empty<Axis>();

    /// <summary>Header displayed above the scatter chart, e.g. <c>"PC1 vs PC2"</c>.</summary>
    [ObservableProperty] private string _chartTitle = "";

    /// <summary>Human-readable total sample count, e.g. <c>"1 024 samples"</c>.</summary>
    [ObservableProperty] private string _sampleCountText = "0 samples";

    /// <summary>
    /// <see langword="true"/> when the underlying model operates in 3-component mode,
    /// enabling the <see cref="Scatter3D"/> monitor and the PC3 variance indicator.
    /// </summary>
    [ObservableProperty] private bool _is3D;

    /// <summary>
    /// When <see langword="true"/>, the 3D projection pane is shown (only meaningful in 3-component
    /// mode). Toggled from the view; a guard keeps at least one of the 3D/2D panes visible.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Show3DPane))]
    private bool _show3DView = true;

    /// <summary>When <see langword="true"/>, the 2D (PC1 vs PC2) scatter pane is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Show2DPane))]
    private bool _show2DView = true;

    /// <summary>Explained variance ratio for the first principal component (PC1), in the range [0, 1].</summary>
    [ObservableProperty] private double _pc1Variance;

    /// <summary>Explained variance ratio for the second principal component (PC2), in the range [0, 1].</summary>
    [ObservableProperty] private double _pc2Variance;

    /// <summary>
    /// Explained variance ratio for the third principal component (PC3), in the range [0, 1].
    /// Only meaningful when <see cref="Is3D"/> is <see langword="true"/>.
    /// </summary>
    [ObservableProperty] private double _pc3Variance;

    #endregion

    #region 3D Scatter

    /// <summary>
    /// Optional Three.js-backed 3D scatter monitor. Created only when the model runs in
    /// 3-component mode; <see langword="null"/> when <see cref="Is3D"/> is <see langword="false"/>.
    /// </summary>
    public Scatter3DMonitor? Scatter3D { get; }

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

    /// <summary>Cached lower bound of the last applied square axis range.</summary>
    private double _currentAxisMin = -5;

    /// <summary>Cached upper bound of the last applied square axis range.</summary>
    private double _currentAxisMax = 5;

    /// <summary>
    /// Set to <see langword="true"/> when at least one bound has changed since the last
    /// <see cref="UpdateAxisBoundsUI"/> call. Consumed and reset on the next timer tick.
    /// </summary>
    private bool _axisBoundsDirty;

    /// <summary>Fractional margin added to each side of the data range when computing axis limits (50 %).</summary>
    private const double AxisMarginFactor = 0.50;

    #endregion

    #region Pending-Update Flags

    /// <summary>Timestamp of the last throttled text-property update (sample count, variance ratios).</summary>
    private DateTime _lastTextUpdate = DateTime.MinValue;

    #endregion

    #region Public Surface

    /// <summary>Exposes the underlying <see cref="OnlinePCA"/> model for direct binding from the view.</summary>
    public OnlinePCA OnlinePCA => _onlinePca;

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

    #region Constructor

    /// <summary>
    /// Initialises the ViewModel, optionally creates the <see cref="Scatter3DMonitor"/> for
    /// 3-component mode, wires up model subscriptions, and registers with the shared
    /// visualisation timer.
    /// </summary>
    /// <param name="onlinePca">The <see cref="OnlinePCA"/> model to visualise. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="onlinePca"/> is <see langword="null"/>.</exception>
    public OnlinePCAViewModel(OnlinePCA onlinePca)
    {
        _onlinePca = onlinePca ?? throw new ArgumentNullException(nameof(onlinePca));
        _is3D = onlinePca.ComponentCount == 3;
        _chartTitle = _is3D ? "PC1 vs PC2 vs PC3" : "PC1 vs PC2";

        _timerCallback = OnTimerTick;

        if (_is3D)
        {
            Scatter3D = new Scatter3DMonitor(maxPoints: _maxScatterPoints)
            {
                XLabel = "PC1",
                YLabel = "PC2",
                ZLabel = "PC3",
            };
        }

        InitializeCharts();

        _onlinePca.AddSubscriber(this);
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
    /// 3D renderer while its pane is hidden so it does not draw frames nobody can see.
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
    /// Creates the default axis and series objects for both the scatter and variance charts.
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
                Name = "PC1",
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
                Name = "PC2",
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
    /// Drains <see cref="_pointBuffer"/>, conditionally re-fits axis bounds, and processes any
    /// pending variance chart update.
    /// </summary>
    private void OnTimerTick()
    {
        if (_disposed) return;

        try
        {
            ProcessPointBuffer();

            if (_axisBoundsDirty)
            {
                UpdateAxisBoundsUI();
                _axisBoundsDirty = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OnlinePCAViewModel] Timer error: {ex.Message}");
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
    /// Receives a projected vector published by <see cref="OnlinePCA"/>.
    /// </summary>
    /// <param name="sender">The publishing block (ignored).</param>
    /// <param name="value">
    /// Expected to be a <c>Vector&lt;double&gt;</c> containing at least two components (PC1, PC2).
    /// When <see cref="Is3D"/> is <see langword="true"/>, a third component (PC3) is also consumed
    /// by the <see cref="Scatter3DMonitor"/>.
    /// </param>
    /// <remarks>
    /// Called on a background thread. PC1/PC2 are buffered in <see cref="_pointBuffer"/> under
    /// <see cref="_lock"/> and axis bounds are updated immediately. If the model is in 3D mode the
    /// full projection is forwarded to <see cref="Scatter3D"/> synchronously. Sample count and
    /// variance ratios are dispatched to the UI thread at most once per 100 ms.
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
            var sampleCount = _onlinePca.SampleCount;
            var varRatio = _onlinePca.ExplainedVarianceRatio;

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                SampleCountText = $"{sampleCount} samples";

                if (varRatio?.Count >= 2)
                {
                    Pc1Variance = varRatio[0];
                    Pc2Variance = varRatio[1];
                    if (Is3D && varRatio.Count >= 3)
                        Pc3Variance = varRatio[2];
                }
            }, DispatcherPriority.Background);
        }
    }

    #endregion

    #region Axis Helpers

    /// <summary>
    /// Re-fits the scatter chart axes to the current data extent using a square aspect ratio
    /// with a 50 % margin. No-ops when fewer than two distinct data points have been seen.
    /// Only called when <see cref="_axisBoundsDirty"/> is set.
    /// </summary>
    private void UpdateAxisBoundsUI()
    {
        double minX, maxX, minY, maxY;

        lock (_lock)
        {
            minX = _minX; maxX = _maxX;
            minY = _minY; maxY = _maxY;
        }

        if (minX >= maxX || minY >= maxY) return;

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

        _currentAxisMin = centerX - halfRange;
        _currentAxisMax = centerX + halfRange;

        if (XAxes?.Length > 0)
        {
            XAxes[0].MinLimit = centerX - halfRange;
            XAxes[0].MaxLimit = centerX + halfRange;
        }
        if (YAxes?.Length > 0)
        {
            YAxes[0].MinLimit = centerY - halfRange;
            YAxes[0].MaxLimit = centerY + halfRange;
        }
    }

    #endregion


    #region IDisposable

    /// <summary>
    /// Unsubscribes from the <see cref="VisualizationTimer"/> and the <see cref="OnlinePCA"/> model,
    /// and disposes the <see cref="Scatter3DMonitor"/> if one was created, preventing further
    /// callbacks after the card is closed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        VisualizationTimer.Instance.Unsubscribe(_timerCallback);
        _onlinePca?.RemoveSubscriber(this);
        Scatter3D?.Dispose();
    }

    #endregion
}
