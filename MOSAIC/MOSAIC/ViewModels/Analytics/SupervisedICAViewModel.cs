using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// ViewModel for the Supervised ICA visualisation card.
/// </summary>
/// <remarks>
/// <para>
/// Bridges the <see cref="SupervisedICA"/> model and the Avalonia UI. It subscribes to the model
/// via <see cref="ISubscriber"/> and receives projected <c>Vector&lt;double&gt;</c> values on a
/// background thread. Incoming points are buffered in <see cref="_pointBuffer"/> and consumed on
/// the shared <see cref="VisualizationTimer"/> tick so that all chart mutations happen on the UI thread.
/// </para>
/// <para>
/// <b>Live stream series:</b> A single <see cref="ScatterSeries{TModel}"/> backed by
/// <see cref="_liveScatterPointsUI"/> shows the continuous real-time IC1/IC2 projection.
/// Points are evicted from the front once <see cref="MaxScatterPoints"/> is reached.
/// </para>
/// <para>
/// <b>Captured cluster series:</b> When the user captures a labelled class via
/// <see cref="StartCaptureCommand"/>, the stored cluster points are rendered as additional
/// <see cref="ScatterSeries{TModel}"/> instances on top of the live stream. The entire series
/// array is rebuilt by <see cref="RebuildAllSeriesUi"/> whenever <see cref="_clusterUpdatePending"/>
/// is set. During an active capture session the rebuild is throttled to once per 500 ms to
/// prevent chart jitter.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="_pointBuffer"/>, <see cref="_liveScatterPoints"/>, the
/// axis-bound tracking fields, and the 3D class-index map are guarded by <see cref="_lock"/>.
/// All LiveCharts mutations happen exclusively on the UI thread via the shared timer callback.
/// </para>
/// </remarks>
public partial class SupervisedICAViewModel : ObservableObject, ISubscriber, IDisposable
{
    #region Fields

    /// <summary>The underlying Supervised ICA model this ViewModel wraps.</summary>
    private readonly SupervisedICA _supervisedIca;

    /// <summary>
    /// Guards <see cref="_pointBuffer"/>, <see cref="_liveScatterPoints"/>, the axis-bound tracking
    /// fields, and the <see cref="_clusterClassMap"/> / <see cref="_nextClassIndex"/> pair.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>Cached delegate registered with <see cref="VisualizationTimer"/> to avoid allocations.</summary>
    private readonly Action _timerCallback;

    /// <summary>Set to <see langword="true"/> by <see cref="Dispose"/> to stop timer and subscriber callbacks.</summary>
    private bool _disposed;

    #endregion

    #region Live Stream State

    /// <summary>
    /// Internal ring buffer of live IC1/IC2 points shared across threads.
    /// Guarded by <see cref="_lock"/>; used to enforce the <see cref="MaxScatterPoints"/> cap.
    /// </summary>
    private readonly List<ObservablePoint> _liveScatterPoints = new();

    /// <summary>
    /// UI-facing observable collection bound to the live-stream scatter series.
    /// Only mutated on the UI thread inside <see cref="ProcessPointBuffer"/>.
    /// </summary>
    private readonly ObservableCollection<ObservablePoint> _liveScatterPointsUI = new();

    #endregion

    #region Point Buffer

    /// <summary>
    /// Staging buffer that collects incoming <c>(x, y)</c> tuples from the background thread.
    /// Drained on every <see cref="VisualizationTimer"/> tick on the UI thread.
    /// </summary>
    private readonly List<(double X, double Y)> _pointBuffer = new();

    #endregion

    #region Observable Properties

    /// <summary>
    /// Maximum number of live-stream scatter points retained before the oldest are evicted.
    /// Changing this value also trims the internal ring buffer and marks axis bounds as dirty.
    /// </summary>
    [ObservableProperty]
    private int _maxScatterPoints = 1000;

    /// <summary>Array of series bound to the main scatter chart (live stream + captured clusters).</summary>
    [ObservableProperty] private ISeries[] _scatterSeries = Array.Empty<ISeries>();

    /// <summary>X-axis configuration for the scatter chart (IC1).</summary>
    [ObservableProperty] private Axis[] _xAxes;

    /// <summary>Y-axis configuration for the scatter chart (IC2).</summary>
    [ObservableProperty] private Axis[] _yAxes;

    /// <summary>Header displayed above the scatter chart, e.g. <c>"IC1 vs IC2"</c>.</summary>
    [ObservableProperty] private string _chartTitle = "";

    /// <summary>Human-readable total sample count, e.g. <c>"1 024 samples"</c>.</summary>
    [ObservableProperty] private string _sampleCountText = "0 samples";

    /// <summary>
    /// <see langword="true"/> when the underlying model operates in 3-component mode,
    /// enabling the <see cref="Scatter3D"/> monitor and the IC3 kurtosis indicator.
    /// </summary>
    [ObservableProperty] private bool _is3D;

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
    /// per-component metric bars. Falls back to <c>1</c> when all components are zero.
    /// </summary>
    public double MaxAbsKurtosis
    {
        get
        {
            double max = Math.Max(Math.Abs(Ic1Kurtosis), Math.Abs(Ic2Kurtosis));
            if (Is3D) max = Math.Max(max, Math.Abs(Ic3Kurtosis));
            return max > 0 ? max : 1;
        }
    }

    /// <summary>
    /// The label text entered by the user before starting a capture session.
    /// Bound to the label input field in the view.
    /// </summary>
    [ObservableProperty]
    private string _labelInput = "";

    #endregion

    #region 3D Scatter

    /// <summary>
    /// Optional Three.js-backed 3D scatter monitor. Created only when the model runs in
    /// 3-component mode; <see langword="null"/> when <see cref="Is3D"/> is <see langword="false"/>.
    /// </summary>
    public Scatter3DMonitor? Scatter3D { get; }

    #endregion

    #region Nested Types

    /// <summary>
    /// Lightweight display model for a single captured ICA cluster, used to populate
    /// the legend in the UI.
    /// </summary>
    public class ClusterInfo
    {
        /// <summary>The human-readable label assigned to this cluster (e.g. <c>"rest"</c>, <c>"pinch"</c>).</summary>
        public string Label { get; set; } = "";

        /// <summary>Number of projected points captured for this cluster.</summary>
        public int PointCount { get; set; }

        /// <summary>Avalonia brush derived from the cluster's hex colour, used to tint the legend swatch.</summary>
        public IBrush? ColorBrush { get; set; }
    }

    #endregion

    #region Public Surface

    /// <summary>
    /// Live collection of <see cref="ClusterInfo"/> items bound to the cluster legend in the view.
    /// Rebuilt by <see cref="RebuildAllSeriesUi"/> on each cluster update.
    /// </summary>
    public ObservableCollection<ClusterInfo> ClusterInfoList { get; } = new();

    /// <summary>
    /// <see langword="true"/> when the model has at least one captured cluster;
    /// controls legend visibility in the view.
    /// </summary>
    public bool HasClusters => _supervisedIca?.CapturedClusters?.Count > 0;

    /// <summary>Exposes the underlying <see cref="SupervisedICA"/> model for direct binding from the view.</summary>
    public SupervisedICA SupervisedICA => _supervisedIca;

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
    /// <see cref="UpdateAxisBoundsUI"/> call.
    /// </summary>
    private bool _axisBoundsDirty = true;

    /// <summary>Fractional margin added to each side of the data range when computing axis limits (30 %).</summary>
    private const double AxisMarginFactor = 0.30;

    #endregion

    #region Pending-Update Flags

    /// <summary>
    /// Set to <see langword="true"/> when the scatter series array needs rebuilding on the next timer tick,
    /// either because a new cluster was captured or an existing one was cleared.
    /// </summary>
    private bool _clusterUpdatePending;

    /// <summary>Timestamp of the last throttled text-property update (sample count, kurtosis values).</summary>
    private DateTime _lastTextUpdate = DateTime.MinValue;

    /// <summary>
    /// Timestamp of the last cluster-series rebuild triggered during an active capture session.
    /// Rebuilds are limited to once per 500 ms while capturing to prevent chart jitter.
    /// </summary>
    private DateTime _lastClusterUpdate = DateTime.MinValue;

    /// <summary>Minimum interval in milliseconds between axis-bound updates.</summary>
    private const int AxisUpdateIntervalMs = 250;

    #endregion

    #region 3D Class Mapping

    /// <summary>
    /// Maps each cluster label to a stable integer class index used by <see cref="Scatter3DMonitor"/>
    /// for per-class colouring. Index 0 is reserved for the unlabelled live stream.
    /// </summary>
    private readonly Dictionary<string, int> _clusterClassMap = new();

    /// <summary>Counter used to assign the next available class index when a new label is encountered.</summary>
    private int _nextClassIndex;

    #endregion

    #region Constructor

    /// <summary>
    /// Initialises the ViewModel, wires up model subscriptions, schedules an initial cluster
    /// rebuild if the model already contains captured data, and registers with the shared
    /// visualisation timer.
    /// </summary>
    /// <param name="supervisedIca">The <see cref="SupervisedICA"/> model to visualise. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="supervisedIca"/> is <see langword="null"/>.</exception>
    public SupervisedICAViewModel(SupervisedICA supervisedIca)
    {
        _supervisedIca = supervisedIca ?? throw new ArgumentNullException(nameof(supervisedIca));
        _is3D = supervisedIca.ComponentCount == 3;
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

        InitializeAxes();
        InitializeSeries();

        _supervisedIca.AddSubscriber(this);
        _supervisedIca.PropertyChanged += OnIcaPropertyChanged;

        if (_supervisedIca.CapturedClusters.Count > 0)
        {
            Console.WriteLine($"[SupervisedICAViewModel] Loading {_supervisedIca.CapturedClusters.Count} existing clusters");
            _clusterUpdatePending = true;
        }

        VisualizationTimer.Instance.Subscribe(_timerCallback);
    }

    #endregion

    #region Initialisation

    /// <summary>
    /// Creates the default axis objects for both the scatter and kurtosis charts.
    /// Called once from the constructor.
    /// </summary>
    private void InitializeAxes()
    {
        XAxes =
        [
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
        ];
        YAxes =
        [
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
        ];
    }

    /// <summary>
    /// Creates the initial series for the scatter chart.
    /// The scatter chart starts with only the live-stream series; captured cluster series are
    /// added later by <see cref="RebuildAllSeriesUi"/>.
    /// Called once from the constructor.
    /// </summary>
    private void InitializeSeries()
    {
        ScatterSeries =
        [
            new ScatterSeries<ObservablePoint>
            {
                Values = _liveScatterPointsUI,
                GeometrySize = 5,
                Fill = new SolidColorPaint(new SKColor(0, 191, 255, 180)),
                Stroke = null,
                Name = "Live Stream"
            }
        ];
    }

    #endregion

    #region Model Event Handlers

    /// <summary>
    /// Reacts to <see cref="SupervisedICA"/> property changes.
    /// Sets <see cref="_clusterUpdatePending"/> whenever the captured-cluster dictionary changes
    /// so the series are rebuilt on the next timer tick.
    /// </summary>
    private void OnIcaPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed) return;

        if (e.PropertyName == nameof(SupervisedICA.CapturedClusters))
        {
            _clusterUpdatePending = true;
        }
    }

    #endregion

    #region Property Callbacks

    /// <summary>
    /// Keeps at least one projection pane visible when the user toggles 3D off, and stops the
    /// 3D renderer while the pane is hidden so it does not keep drawing frames nobody can see.
    /// </summary>
    partial void OnShow3DViewChanged(bool value)
    {
        if (!value && !Show2DView) Show2DView = true;

        // Hiding the pane only sets IsVisible=false, which does not detach the view, so the
        // monitor has to be paused explicitly.
        if (value) Scatter3D?.Resume();
        else Scatter3D?.Pause();
    }

    /// <summary>Keeps at least one projection pane visible when the user toggles 2D off.</summary>
    partial void OnShow2DViewChanged(bool value)
    {
        if (!value && !Show3DView) Show3DView = true;
    }

    /// <summary>
    /// Trims both the internal ring buffer and the UI collection to the new cap, syncs
    /// <see cref="Scatter3DMonitor.MaxPoints"/>, and marks axis bounds as dirty when
    /// <see cref="MaxScatterPoints"/> changes.
    /// </summary>
    partial void OnMaxScatterPointsChanged(int value)
    {
        lock (_lock)
        {
            while (_liveScatterPoints.Count > value)
            {
                _liveScatterPoints.RemoveAt(0);
            }
        }

        while (_liveScatterPointsUI.Count > value)
            _liveScatterPointsUI.RemoveAt(0);

        if (Scatter3D != null)
            Scatter3D.MaxPoints = value;

        _axisBoundsDirty = true;
    }

    #endregion

    #region Timer Callback

    /// <summary>
    /// Called on the UI thread by <see cref="VisualizationTimer"/> at its configured interval.
    /// Drains <see cref="_pointBuffer"/>, refreshes axis bounds, and processes any pending
    /// kurtosis or cluster-series updates. A failed 3D rebuild leaves
    /// <see cref="_clusterUpdatePending"/> set so the next tick retries it.
    /// </summary>
    private void OnTimerTick()
    {
        if (_disposed) return;

        try
        {
            ProcessPointBuffer();
            UpdateAxisBoundsUI();

            if (_clusterUpdatePending)
            {
                RebuildAllSeriesUi();
                if (RebuildFrozen3DPoints())
                    _clusterUpdatePending = false;
                OnPropertyChanged(nameof(HasClusters));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SupervisedICAViewModel] Timer error: {ex.Message}");
        }
    }

    #endregion

    #region Point Processing

    /// <summary>
    /// Drains <see cref="_pointBuffer"/> and appends the batched points to both the internal
    /// ring buffer and <see cref="_liveScatterPointsUI"/>, evicting the oldest entry from the
    /// front when <see cref="MaxScatterPoints"/> is exceeded.
    /// </summary>
    private void ProcessPointBuffer()
    {
        List<(double X, double Y)> points;

        lock (_lock)
        {
            if (_pointBuffer.Count == 0) return;

            points = [.._pointBuffer];
            _pointBuffer.Clear();
        }

        foreach (var (x, y) in points)
        {
            lock (_lock)
            {
                if (_liveScatterPoints.Count >= MaxScatterPoints)
                    _liveScatterPoints.RemoveAt(0);
                _liveScatterPoints.Add(new ObservablePoint(x, y));
            }

            if (_liveScatterPointsUI.Count >= MaxScatterPoints)
                _liveScatterPointsUI.RemoveAt(0);
            _liveScatterPointsUI.Add(new ObservablePoint(x, y));
        }
    }

    #endregion

    #region ISubscriber

    /// <summary>
    /// Receives a projected vector published by <see cref="SupervisedICA"/>.
    /// </summary>
    /// <param name="sender">The publishing block (ignored).</param>
    /// <param name="value">
    /// Expected to be a <c>Vector&lt;double&gt;</c> containing at least two components (IC1, IC2).
    /// </param>
    /// <remarks>
    /// Called on a background thread. The IC1/IC2 coordinates are buffered in
    /// <see cref="_pointBuffer"/> under <see cref="_lock"/> and axis bounds are updated
    /// immediately. Sample count and kurtosis values are dispatched to the UI thread at most
    /// once per 100 ms. When a capture session is active, the cluster series rebuild is
    /// additionally throttled to once per 500 ms to prevent chart jitter.
    /// </remarks>
    public void ReceiveInput(object sender, object value)
    {
        if (_disposed) return;
        if (value is not MathNet.Numerics.LinearAlgebra.Vector<double> projection) return;
        if (projection.Count < 2) return;

        double x = projection[0];
        double y = projection[1];

        if (_is3D && projection.Count >= 3)
        {
            int classIdx = 0;
            if (_supervisedIca.IsCapturing && !string.IsNullOrEmpty(_supervisedIca.CurrentLabel))
                classIdx = GetClassIndex(_supervisedIca.CurrentLabel);
            Scatter3D?.AddPoint(x, y, projection[2], classIdx);
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
            var sampleCount = _supervisedIca.SampleCount;
            var varRatio = _supervisedIca.Kurtosis;

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                SampleCountText = $"{sampleCount} samples";

                if (varRatio?.Count >= 2)
                {
                    Ic1Kurtosis = varRatio[0];
                    Ic2Kurtosis = varRatio[1];
                    if (Is3D && varRatio.Count >= 3)
                        Ic3Kurtosis = varRatio[2];
                }
            }, DispatcherPriority.Background);
        }

        if (!_supervisedIca.IsCapturing || !((now - _lastClusterUpdate).TotalMilliseconds >= 500)) return;
        _lastClusterUpdate = now;
        _clusterUpdatePending = true;
    }

    #endregion

    #region 3D Class Mapping

    /// <summary>
    /// Returns the stable integer class index for the given label, creating a new mapping entry
    /// if the label has not been seen before. Index 0 is reserved for the live stream.
    /// </summary>
    /// <param name="label">The cluster label to look up or register.</param>
    /// <returns>A positive integer class index unique to this label within the session.</returns>
    /// <remarks>
    /// Called from both the data thread (<see cref="ReceiveInput"/>) and the UI thread
    /// (<see cref="RebuildFrozen3DPoints"/>), so the lookup and the assignment of a new index
    /// are taken together under <see cref="_lock"/>.
    /// </remarks>
    private int GetClassIndex(string label)
    {
        lock (_lock)
        {
            if (_clusterClassMap.TryGetValue(label, out int idx)) return idx;
            idx = ++_nextClassIndex;
            _clusterClassMap[label] = idx;
            return idx;
        }
    }

    #endregion

    #region Commands

    /// <summary>
    /// Begins a labelled capture session using the current <see cref="LabelInput"/> text.
    /// No-op when <see cref="LabelInput"/> is null, empty, or whitespace.
    /// </summary>
    [RelayCommand]
    private void StartCapture()
    {
        if (string.IsNullOrWhiteSpace(LabelInput))
        {
            Console.WriteLine("[SupervisedICAViewModel] Cannot start capture: label is empty");
            return;
        }

        _supervisedIca.StartCapture(LabelInput.Trim());
    }

    /// <summary>
    /// Ends the current capture session and schedules a cluster-series rebuild.
    /// </summary>
    [RelayCommand]
    private void StopCapture()
    {
        _supervisedIca.StopCapture();
        _clusterUpdatePending = true;
    }

    /// <summary>
    /// Removes a single named cluster from the model and schedules a cluster-series rebuild.
    /// </summary>
    /// <param name="label">The cluster label to remove. No-op when <see langword="null"/> or empty.</param>
    [RelayCommand]
    private void ClearCluster(string? label)
    {
        if (string.IsNullOrEmpty(label)) return;
        _supervisedIca.ClearCluster(label);
        _clusterUpdatePending = true;
    }

    /// <summary>
    /// Removes all clusters from the model, resets the class-index map, clears the
    /// <see cref="Scatter3DMonitor"/> entirely along with its class colours, drops the buffered
    /// and live 2D scatter points, resets axis bounds to their defaults, and schedules a
    /// cluster-series rebuild.
    /// </summary>
    [RelayCommand]
    private void ClearAllClusters()
    {
        _supervisedIca.ClearAllClusters();

        lock (_lock)
        {
            _clusterClassMap.Clear();
            _nextClassIndex = 0;

            _minX = _minY = -5;
            _maxX = _maxY = 5;

            _liveScatterPoints.Clear();

            // Points already buffered here were handed to the 3D monitor directly by
            // ReceiveInput, so the Clear() below discards them: draining them on the next tick
            // would put them back into the 2D pane alone.
            _pointBuffer.Clear();
        }

        // The live ring buffer still carries the class indices of the deleted labels, so those
        // points would be recoloured as the next capture reuses the indices: drop them as well.
        Scatter3D?.Clear();
        Scatter3D?.ClearClassColors();

        // Same UI thread as ProcessPointBuffer, the only other writer of this collection.
        _liveScatterPointsUI.Clear();

        _axisBoundsDirty = true;
        _clusterUpdatePending = true;
    }

    #endregion

    #region Chart Refresh

    /// <summary>
    /// Rebuilds the frozen cluster point sets in the <see cref="Scatter3DMonitor"/> from a
    /// snapshot of <see cref="SupervisedICA.CapturedClusters"/>, and syncs per-class colours
    /// from <see cref="SupervisedICA.ClusterColors"/>. No-ops when not in 3D mode.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the monitor is up to date, either because the rebuild
    /// completed or because there is nothing to rebuild in 2-component mode;
    /// <see langword="false"/> when the rebuild failed and has to be retried.
    /// </returns>
    private bool RebuildFrozen3DPoints()
    {
        if (Scatter3D == null || !_is3D) return true;

        try
        {
            var allPts = new List<Point3D>();
            var allCls = new List<int>();

            var colorSnapshot = _supervisedIca.ClusterColors
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            foreach (var (label, hexColor) in colorSnapshot)
            {
                int cls = GetClassIndex(label);
                Scatter3D.SetClassColor(cls, SKColor.Parse(hexColor));
            }

            var clusterSnapshot = _supervisedIca.CapturedClusters
                .Select(kvp => (Label: kvp.Key, Points: kvp.Value.ToList()))
                .ToList();

            foreach (var (label, points) in clusterSnapshot)
            {
                int cls = GetClassIndex(label);
                foreach (var p in points)
                {
                    if (p.Count >= 3)
                    {
                        allPts.Add(new Point3D(p[0], p[1], p[2]));
                        allCls.Add(cls);
                    }
                }
            }

            if (allPts.Count > 0)
                Scatter3D.SetFrozenPoints(allPts.ToArray(), allCls.ToArray());
            else
                Scatter3D.ClearFrozenPoints();

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SupervisedICAViewModel] Frozen 3D rebuild error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Rebuilds the entire <see cref="ScatterSeries"/> array from a snapshot of the model's
    /// captured clusters, then appends the live-stream series on top.
    /// Also rebuilds <see cref="ClusterInfoList"/> for the legend.
    /// </summary>
    /// <remarks>
    /// Takes point-list snapshots of both <see cref="SupervisedICA.CapturedClusters"/> and
    /// <see cref="SupervisedICA.ClusterColors"/> before iterating to avoid collection-modified
    /// exceptions if the model updates concurrently.
    /// </remarks>
    private void RebuildAllSeriesUi()
    {
        var seriesList = new List<ISeries>();
        ClusterInfoList.Clear();

        try
        {
            var clusterSnapshot = _supervisedIca.CapturedClusters
                .Select(kvp => (Label: kvp.Key, Points: kvp.Value.ToList()))
                .ToList();

            var colorSnapshot = _supervisedIca.ClusterColors
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            foreach (var (label, points) in clusterSnapshot)
            {
                var color = colorSnapshot.TryGetValue(label, out var c) ? c : "#808080";
                var skColor = SKColor.Parse(color);

                var clusterPoints = new ObservableCollection<ObservablePoint>(
                    points.Where(p => p.Count >= 2).Select(p => new ObservablePoint(p[0], p[1]))
                );

                seriesList.Add(new ScatterSeries<ObservablePoint>
                {
                    Values = clusterPoints,
                    GeometrySize = 10,
                    Fill = new SolidColorPaint(new SKColor(skColor.Red, skColor.Green, skColor.Blue, 120)),
                    Stroke = new SolidColorPaint(skColor, 2),
                    Name = label
                });

                ClusterInfoList.Add(new ClusterInfo
                {
                    Label = label,
                    PointCount = points.Count,
                    ColorBrush = new SolidColorBrush(Color.Parse(color))
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SupervisedICAViewModel] Error building cluster series: {ex.Message}");
        }

        // Live stream series is always rendered on top
        seriesList.Add(new ScatterSeries<ObservablePoint>
        {
            Values = _liveScatterPointsUI,
            GeometrySize = 5,
            Fill = new SolidColorPaint(new SKColor(0, 191, 255, 180)),
            Stroke = null,
            Name = "Live Stream"
        });

        ScatterSeries = seriesList.ToArray();
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

        double currentXMin = _xAxes?[0]?.MinLimit ?? -5;
        double currentXMax = _xAxes?[0]?.MaxLimit ?? 5;
        double currentYMin = _yAxes?[0]?.MinLimit ?? -5;
        double currentYMax = _yAxes?[0]?.MaxLimit ?? 5;

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

        if (_xAxes?.Length > 0)
        {
            _xAxes[0].MinLimit = newXMin;
            _xAxes[0].MaxLimit = newXMax;
        }

        if (_yAxes?.Length > 0)
        {
            _yAxes[0].MinLimit = newYMin;
            _yAxes[0].MaxLimit = newYMax;
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Unsubscribes from the <see cref="VisualizationTimer"/> and the <see cref="SupervisedICA"/> model,
    /// and disposes the <see cref="Scatter3DMonitor"/> if one was created, preventing further
    /// timer ticks or subscriber callbacks after the card is closed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        VisualizationTimer.Instance.Unsubscribe(_timerCallback);

        if (_supervisedIca != null)
        {
            _supervisedIca.RemoveSubscriber(this);
            _supervisedIca.PropertyChanged -= OnIcaPropertyChanged;
        }

        Scatter3D?.Dispose();
    }

    #endregion
}