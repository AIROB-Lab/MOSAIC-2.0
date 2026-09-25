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
/// ViewModel for the Online LDA visualisation card.
/// </summary>
/// <remarks>
/// <para>
/// Bridges the <see cref="OnlineLDA"/> model and the Avalonia UI. It subscribes to the model
/// via <see cref="ISubscriber"/> and receives <c>(label, projection)</c> tuples on a background
/// thread. Incoming points are buffered in <see cref="_pointBuffer"/> and consumed on the
/// shared <see cref="VisualizationTimer"/> tick so that all chart mutations happen on the UI thread.
/// </para>
/// <para>
/// <b>Scatter chart:</b> Each labelled class gets its own <see cref="ScatterSeries{TModel}"/>
/// backed by an <see cref="ObservableCollection{T}"/> of <see cref="ObservablePoint"/>. Series are
/// created lazily the first time a label is encountered and are never recreated — only their
/// underlying collections are mutated, which keeps LiveCharts animations smooth.
/// </para>
/// <para>
/// <b>Point cap:</b> Per-class point counts are bounded by
/// <c>max(<see cref="MaxScatterPoints"/> / classCount, 50)</c> so total scatter points stay
/// near the configured ceiling regardless of how many classes exist.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="_pointBuffer"/> and the axis-bound tracking fields are
/// guarded by <see cref="_lock"/>. All LiveCharts mutations happen exclusively on the UI thread
/// via the shared timer callback.
/// </para>
/// </remarks>
public partial class OnlineLDAViewModel : ObservableObject, ISubscriber, IDisposable
{
    #region Fields

    /// <summary>The underlying Online LDA model this ViewModel wraps.</summary>
    private readonly OnlineLDA _onlineLda;

    /// <summary>Guards <see cref="_pointBuffer"/> and the axis-bound tracking fields.</summary>
    private readonly object _lock = new();

    /// <summary>Cached delegate registered with <see cref="VisualizationTimer"/> to avoid allocations.</summary>
    private readonly Action _timerCallback;

    /// <summary>Set to <see langword="true"/> by <see cref="Dispose"/> to stop timer and subscriber callbacks.</summary>
    private bool _disposed;

    #endregion

    #region Point Buffer

    /// <summary>
    /// Staging buffer that collects incoming <c>(label, x, y)</c> tuples from the background
    /// thread. Drained on every <see cref="VisualizationTimer"/> tick on the UI thread.
    /// </summary>
    private readonly List<(string Label, double X, double Y)> _pointBuffer = new();

    #endregion

    #region Per-Label State

    /// <summary>Maps each class label to its live <see cref="ObservablePoint"/> collection.</summary>
    private readonly Dictionary<string, ObservableCollection<ObservablePoint>> _labelPoints = new();

    /// <summary>Maps each class label to the <see cref="ScatterSeries{TModel}"/> that renders it.</summary>
    private readonly Dictionary<string, ScatterSeries<ObservablePoint>> _labelSeries = new();

    /// <summary>Master list of all series handed to the scatter chart; kept in sync with <see cref="_labelSeries"/>.</summary>
    private readonly ObservableCollection<ISeries> _allSeries = new();

    #endregion

    #region Observable Properties

    /// <summary>
    /// Maximum total scatter points across all classes before older points are evicted.
    /// Per-class cap is derived as <c>max(MaxScatterPoints / classCount, 50)</c>.
    /// </summary>
    [ObservableProperty] private int _maxScatterPoints = 1000;

    /// <summary>Array of series bound to the main scatter chart.</summary>
    [ObservableProperty] private ISeries[] _scatterSeries = Array.Empty<ISeries>();

    /// <summary>X-axis configuration for the scatter chart (LD1).</summary>
    [ObservableProperty] private Axis[] _xAxes = Array.Empty<Axis>();

    /// <summary>Y-axis configuration for the scatter chart (LD2).</summary>
    [ObservableProperty] private Axis[] _yAxes = Array.Empty<Axis>();

    /// <summary>Header displayed above the scatter chart, e.g. <c>"LD1 vs LD2"</c>.</summary>
    [ObservableProperty] private string _chartTitle = "";

    /// <summary>Human-readable total sample count, e.g. <c>"1 024 samples"</c>.</summary>
    [ObservableProperty] private string _sampleCountText = "0 samples";

    /// <summary>Human-readable class count, e.g. <c>"3 classes"</c>.</summary>
    [ObservableProperty] private string _classCountText = "0 classes";

    /// <summary>
    /// <see langword="true"/> when the underlying model operates in 3-component mode,
    /// enabling the <see cref="Scatter3D"/> monitor and the LD3 separability indicator.
    /// </summary>
    [ObservableProperty] private bool _is3D;

    /// <summary>
    /// When <see langword="true"/>, the 3D projection pane is shown (only meaningful in 3-component
    /// mode). Toggled from the view; a guard keeps at least one of the 3D/2D panes visible.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Show3DPane))]
    private bool _show3DView = true;

    /// <summary>When <see langword="true"/>, the 2D (LD1 vs LD2) scatter pane is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Show2DPane))]
    private bool _show2DView = true;

    /// <summary>Normalised separability score for the first linear discriminant (LD1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxSeparability))]
    private double _ld1Separability;

    /// <summary>Normalised separability score for the second linear discriminant (LD2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxSeparability))]
    private double _ld2Separability;

    /// <summary>
    /// Normalised separability score for the third linear discriminant (LD3).
    /// Only meaningful when <see cref="Is3D"/> is <see langword="true"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxSeparability))]
    private double _ld3Separability;

    /// <summary>
    /// Largest separability magnitude across the active discriminants, used as the
    /// normalisation denominator for the per-component separability bars.
    /// </summary>
    public double MaxSeparability
    {
        get
        {
            var m = System.Math.Max(System.Math.Abs(Ld1Separability), System.Math.Abs(Ld2Separability));
            if (Is3D) m = System.Math.Max(m, System.Math.Abs(Ld3Separability));
            return m;
        }
    }

    #endregion

    #region 3D Scatter

    /// <summary>
    /// Optional 3D scatter monitor. Created only when the model runs in
    /// 3-component mode; <see langword="null"/> when <see cref="Is3D"/> is <see langword="false"/>.
    /// </summary>
    public Scatter3DMonitor? Scatter3D { get; }

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

    #region Axis Bounds

    /// <summary>Running minimum X value across all received projections; used for dynamic axis scaling.</summary>
    private double _minX = double.MaxValue;

    /// <summary>Running maximum X value across all received projections; used for dynamic axis scaling.</summary>
    private double _maxX = double.MinValue;

    /// <summary>Running minimum Y value across all received projections; used for dynamic axis scaling.</summary>
    private double _minY = double.MaxValue;

    /// <summary>Running maximum Y value across all received projections; used for dynamic axis scaling.</summary>
    private double _maxY = double.MinValue;

    #endregion

    #region Pending-Update Flags

    /// <summary>
    /// Set to <see langword="true"/> when <see cref="ClusterInfoList"/> needs rebuilding on the next timer tick.
    /// </summary>
    private bool _clusterInfoUpdatePending;

    /// <summary>
    /// Set to <see langword="true"/> when the captured-cluster set has actually changed and the
    /// <see cref="Scatter3DMonitor"/> frozen points need rebuilding on the next timer tick.
    /// Deliberately separate from <see cref="_clusterInfoUpdatePending"/>, which is also raised by the
    /// throttled text refresh in <see cref="ReceiveInput"/> and would otherwise re-clone the entire
    /// captured set ten times a second.
    /// </summary>
    private bool _frozen3DUpdatePending;

    /// <summary>Timestamp of the last throttled text-property update (sample count, class count, separability values).</summary>
    private DateTime _lastTextUpdate = DateTime.MinValue;

    /// <summary>Timestamp of the last throttled frozen-3D rebuild requested while a capture is running.</summary>
    private DateTime _lastClusterUpdate = DateTime.MinValue;

    #endregion

    #region Nested Types

    /// <summary>
    /// Lightweight display model for a single captured LDA cluster, used to populate
    /// the legend in the UI.
    /// </summary>
    public class ClusterInfo
    {
        /// <summary>The human-readable label assigned to this cluster (e.g. <c>"rest"</c>, <c>"grasp"</c>).</summary>
        public string Label { get; set; } = "";

        /// <summary>Number of projected points currently retained for this cluster.</summary>
        public int PointCount { get; set; }

        /// <summary>Avalonia brush derived from the cluster's hex colour, used to tint the legend swatch.</summary>
        public IBrush? ColorBrush { get; set; }
    }

    #endregion

    #region Public Surface

    /// <summary>
    /// Live collection of <see cref="ClusterInfo"/> items bound to the cluster legend in the view.
    /// Excludes the transient <c>"default"</c> (unlabelled live-data) series.
    /// </summary>
    public ObservableCollection<ClusterInfo> ClusterInfoList { get; } = new();

    /// <summary>
    /// <see langword="true"/> when <see cref="ClusterInfoList"/> contains at least one entry;
    /// controls legend visibility in the view.
    /// </summary>
    public bool HasClusters => ClusterInfoList.Count > 0;

    /// <summary>Exposes the underlying <see cref="OnlineLDA"/> model for direct binding from the view.</summary>
    public OnlineLDA OnlineLDA => _onlineLda;

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
    /// Initialises the ViewModel, wires up model subscriptions, and loads any clusters already
    /// captured by the model (e.g. when the card is reopened mid-session).
    /// </summary>
    /// <param name="onlineLda">The <see cref="OnlineLDA"/> model to visualise. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="onlineLda"/> is <see langword="null"/>.</exception>
    public OnlineLDAViewModel(OnlineLDA onlineLda)
    {
        _onlineLda = onlineLda ?? throw new ArgumentNullException(nameof(onlineLda));
        _is3D = onlineLda.ComponentCount == 3;
        _chartTitle = _is3D ? "LD1 vs LD2 vs LD3" : "LD1 vs LD2";

        _timerCallback = OnTimerTick;

        if (_is3D)
        {
            Scatter3D = new Scatter3DMonitor(maxPoints: _maxScatterPoints)
            {
                XLabel = "LD1",
                YLabel = "LD2",
                ZLabel = "LD3",
            };
        }

        InitializeCharts();

        _onlineLda.AddSubscriber(this);
        _onlineLda.PropertyChanged += OnLdaPropertyChanged;

        LoadExistingClusters();

        VisualizationTimer.Instance.Subscribe(_timerCallback);
    }

    #endregion

    #region Initialisation

    /// <summary>
    /// Creates the default axis and series objects for both the scatter and separability charts.
    /// Called once from the constructor; series are later populated lazily as labels arrive.
    /// </summary>
    private void InitializeCharts()
    {
        ScatterSeries = Array.Empty<ISeries>();
        XAxes = new Axis[]
        {
            new Axis
            {
                Name = "LD1",
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
                Name = "LD2",
                NameTextSize = 12,
                NamePaint = new SolidColorPaint(SKColors.WhiteSmoke),
                LabelsPaint = new SolidColorPaint(SKColors.Gray),
                SeparatorsPaint = new SolidColorPaint(new SKColor(64, 64, 64)),
                MinLimit = -5,
                MaxLimit = 5
            }
        };
    }

    /// <summary>
    /// Replays all points already stored in <see cref="OnlineLDA.CapturedClusters"/> into the
    /// per-label <see cref="ObservableCollection{T}"/> collections and, in 3-component mode, into
    /// the <see cref="Scatter3DMonitor"/>, so both panes are populated immediately when the card is
    /// opened after data has already been captured.
    /// </summary>
    private void LoadExistingClusters()
    {
        try
        {
            var clusters = _onlineLda.CapturedClusters;
            if (clusters == null || clusters.Count == 0) return;

            Console.WriteLine($"[OnlineLDAViewModel] Loading {clusters.Count} existing clusters");

            foreach (var (label, points) in clusters)
            {
                EnsureLabelExists(label);

                var collection = _labelPoints[label];
                foreach (var point in points)
                {
                    if (point.Count >= 2)
                    {
                        double x = point[0];
                        double y = point[1];
                        collection.Add(new ObservablePoint(x, y));
                        UpdateBounds(x, y);
                    }
                }
            }

            UpdateSeriesArray();
            UpdateAxisBounds();
            UpdateClusterInfo();
            RebuildFrozen3DPoints();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OnlineLDAViewModel] Error loading clusters: {ex.Message}");
        }
    }

    #endregion

    #region Model Event Handlers

    /// <summary>
    /// Reacts to <see cref="OnlineLDA"/> property changes.
    /// Sets <see cref="_clusterInfoUpdatePending"/> and <see cref="_frozen3DUpdatePending"/> whenever
    /// the captured-cluster dictionary changes so the legend and the frozen 3D points are refreshed
    /// on the next timer tick.
    /// </summary>
    private void OnLdaPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed) return;

        if (e.PropertyName == nameof(OnlineLDA.CapturedClusters))
        {
            _clusterInfoUpdatePending = true;
            _frozen3DUpdatePending = true;
        }
    }

    #endregion

    #region Property Callbacks

    /// <summary>
    /// Keeps at least one projection pane visible when the user toggles 3D off, and stops the
    /// <see cref="Scatter3DMonitor"/> renderer while the pane is hidden. Hiding the pane only sets
    /// <c>IsVisible</c>, which does not detach the view from the visual tree, so the monitor would
    /// otherwise keep drawing frames nobody can see.
    /// </summary>
    partial void OnShow3DViewChanged(bool value)
    {
        if (!value && !Show2DView) Show2DView = true;

        if (value) Scatter3D?.Resume();
        else Scatter3D?.Pause();
    }

    /// <summary>Keeps at least one projection pane visible when the user toggles 2D off.</summary>
    partial void OnShow2DViewChanged(bool value)
    {
        if (!value && !Show3DView) Show3DView = true;
    }

    /// <summary>
    /// Syncs the <see cref="Scatter3DMonitor"/> ring-buffer size when <see cref="MaxScatterPoints"/>
    /// changes. The 2D per-class cap is derived on the fly in <see cref="ProcessPointBuffer"/>, so
    /// nothing else needs trimming here.
    /// </summary>
    partial void OnMaxScatterPointsChanged(int value)
    {
        if (Scatter3D != null)
            Scatter3D.MaxPoints = value;
    }

    #endregion

    #region Timer Callback

    /// <summary>
    /// Called on the UI thread by <see cref="VisualizationTimer"/> at its configured interval.
    /// Drains <see cref="_pointBuffer"/>, refreshes axis bounds, and processes any pending
    /// cluster-legend or frozen-3D updates.
    /// </summary>
    private void OnTimerTick()
    {
        if (_disposed) return;

        try
        {
            ProcessPointBuffer();
            UpdateAxisBounds();

            if (_clusterInfoUpdatePending)
            {
                UpdateClusterInfo();
                _clusterInfoUpdatePending = false;
            }

            if (_frozen3DUpdatePending && RebuildFrozen3DPoints())
                _frozen3DUpdatePending = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OnlineLDAViewModel] Timer error: {ex.Message}");
        }
    }

    #endregion

    #region Point Processing

    /// <summary>
    /// Drains <see cref="_pointBuffer"/> and appends the batched points to the appropriate
    /// per-label <see cref="ObservableCollection{T}"/>, creating new series as needed.
    /// Older points are evicted when the per-class cap is exceeded.
    /// </summary>
    private void ProcessPointBuffer()
    {
        List<(string Label, double X, double Y)> points;

        lock (_lock)
        {
            if (_pointBuffer.Count == 0) return;
            points = new List<(string, double, double)>(_pointBuffer);
            _pointBuffer.Clear();
        }

        bool seriesChanged = false;

        foreach (var (label, x, y) in points)
        {
            if (EnsureLabelExists(label))
            {
                seriesChanged = true;
            }

            var collection = _labelPoints[label];

            int maxPerClass = Math.Max(50, MaxScatterPoints / Math.Max(1, _labelPoints.Count));
            while (collection.Count >= maxPerClass)
            {
                collection.RemoveAt(0);
            }

            collection.Add(new ObservablePoint(x, y));
        }

        if (seriesChanged)
        {
            UpdateSeriesArray();
        }
    }

    /// <summary>
    /// Ensures that a scatter series and point collection exist for the given label.
    /// The series is styled differently for the transient <c>"default"</c> (unlabelled) class
    /// versus named captured clusters.
    /// </summary>
    /// <param name="label">The class label to check or create.</param>
    /// <returns>
    /// <see langword="true"/> if a new series was created; <see langword="false"/> if it already existed.
    /// </returns>
    private bool EnsureLabelExists(string label)
    {
        if (_labelPoints.ContainsKey(label))
            return false;

        var points = new ObservableCollection<ObservablePoint>();
        _labelPoints[label] = points;

        SKColor color;
        if (label == "default")
        {
            color = new SKColor(0, 191, 255); // #00BFFF — cyan, matches PCA live-data style
        }
        else if (_onlineLda.ClusterColors.TryGetValue(label, out var hexColor))
        {
            color = SKColor.Parse(hexColor);
        }
        else
        {
            color = new SKColor(128, 128, 128); // Fallback gray
        }

        var series = new ScatterSeries<ObservablePoint>
        {
            Values = points,
            GeometrySize = label == "default" ? 6 : 8,
            Fill = new SolidColorPaint(new SKColor(color.Red, color.Green, color.Blue, label == "default" ? (byte)150 : (byte)200)),
            Stroke = new SolidColorPaint(label == "default" ? new SKColor(0, 255, 136) : color, 1),
            Name = label == "default" ? "Live" : label
        };
        _labelSeries[label] = series;
        _allSeries.Add(series);

        if (label != "default")
        {
            Console.WriteLine($"[OnlineLDAViewModel] Created series for '{label}' with color #{color.Red:X2}{color.Green:X2}{color.Blue:X2}");
            _clusterInfoUpdatePending = true;
            _frozen3DUpdatePending = true;
        }

        return true;
    }

    /// <summary>
    /// Rebuilds <see cref="ScatterSeries"/> from the master <see cref="_allSeries"/> collection.
    /// Called after a new label series is created.
    /// </summary>
    private void UpdateSeriesArray()
    {
        ScatterSeries = _allSeries.ToArray();
    }

    #endregion

    #region ISubscriber

    /// <summary>
    /// Receives a projected point published by <see cref="OnlineLDA"/>.
    /// </summary>
    /// <param name="sender">The publishing block (ignored).</param>
    /// <param name="value">
    /// Expected to be a <c>(string label, Vector&lt;double&gt; projection)</c> tuple where
    /// <c>projection</c> contains at least two components (LD1, LD2).
    /// </param>
    /// <remarks>
    /// Called on a background thread. The point is buffered in <see cref="_pointBuffer"/> under
    /// <see cref="_lock"/> and axis bounds are updated immediately. Text properties (sample count,
    /// class count, separability) are dispatched to the UI thread at most once per 100 ms, and while a
    /// capture is running a frozen-3D rebuild is requested at most once per 500 ms.
    /// </remarks>
    public void ReceiveInput(object sender, object value)
    {
        if (_disposed) return;

        if (value is not (string label, MathNet.Numerics.LinearAlgebra.Vector<double> projection))
            return;

        if (projection.Count < 2) return;

        double x = projection[0];
        double y = projection[1];

        if (Is3D && projection.Count >= 3)
        {
            // Class index 0 is reserved for the unlabelled live stream, which LDA labels "default"
            int classIdx = label == "default" ? 0 : GetClassIndex(label);
            Scatter3D?.AddPoint(x, y, projection[2], classIdx);
        }

        lock (_lock)
        {
            _pointBuffer.Add((label, x, y));
            UpdateBounds(x, y);
        }

        var now = DateTime.UtcNow;
        if ((now - _lastTextUpdate).TotalMilliseconds >= 100)
        {
            _lastTextUpdate = now;
            var sampleCount = _onlineLda.SampleCount;
            var classCount = _onlineLda.ClassCount;
            var separability = _onlineLda.SeparabilityScores;

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                SampleCountText = $"{sampleCount} samples";
                ClassCountText = $"{classCount} classes";

                if (separability?.Count >= 2)
                {
                    Ld1Separability = separability[0];
                    Ld2Separability = separability[1];
                    if (Is3D && separability.Count >= 3)
                        Ld3Separability = separability[2];
                }
            }, DispatcherPriority.Background);

            _clusterInfoUpdatePending = true;
        }

        // OnlineLDA appends captured samples without raising PropertyChanged, so while a capture
        // runs the growing cluster exists only in the monitor's live ring buffer and its earliest
        // points scroll out of it once the capture exceeds MaxScatterPoints
        if (_onlineLda.IsCapturing && (now - _lastClusterUpdate).TotalMilliseconds >= 500)
        {
            _lastClusterUpdate = now;
            _frozen3DUpdatePending = true;
        }
    }

    #endregion

    #region Axis Helpers

    /// <summary>
    /// Extends the running axis bounds to include the point <c>(x, y)</c>.
    /// Must be called under <see cref="_lock"/>.
    /// </summary>
    /// <param name="x">LD1 coordinate.</param>
    /// <param name="y">LD2 coordinate.</param>
    private void UpdateBounds(double x, double y)
    {
        if (x < _minX) _minX = x;
        if (x > _maxX) _maxX = x;
        if (y < _minY) _minY = y;
        if (y > _maxY) _maxY = y;
    }

    /// <summary>
    /// Applies the current axis bounds to the scatter chart axes, adding a 30 % margin
    /// and enforcing a square aspect ratio so that both axes use the same scale.
    /// No-ops when fewer than two distinct data points have been seen.
    /// </summary>
    private void UpdateAxisBounds()
    {
        double minX, maxX, minY, maxY;

        lock (_lock)
        {
            minX = _minX;
            maxX = _maxX;
            minY = _minY;
            maxY = _maxY;
        }

        if (minX >= maxX || minY >= maxY) return;

        double rangeX = maxX - minX;
        double rangeY = maxY - minY;

        if (rangeX < 0.1) rangeX = 1.0;
        if (rangeY < 0.1) rangeY = 1.0;

        double maxRange = Math.Max(rangeX, rangeY);
        double margin = maxRange * 0.30;
        double totalRange = maxRange + 2 * margin;

        double centerX = (minX + maxX) / 2;
        double centerY = (minY + maxY) / 2;
        double halfRange = totalRange / 2;

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

    #region 3D Class Mapping

    /// <summary>
    /// Returns the stable integer class index for the given label, creating a new mapping entry
    /// if the label has not been seen before. Index 0 is reserved for the live stream.
    /// </summary>
    /// <param name="label">The cluster label to look up or register.</param>
    /// <returns>A positive integer class index unique to this label within the session.</returns>
    /// <remarks>
    /// Called from both the data thread (via <see cref="ReceiveInput"/>) and the UI thread (via
    /// <see cref="RebuildFrozen3DPoints"/>), so the lookup and the counter increment are taken
    /// together under <see cref="_lock"/>; neither caller holds the lock already.
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

    #region Chart Refresh

    /// <summary>
    /// Rebuilds the frozen cluster point sets in the <see cref="Scatter3DMonitor"/> from a
    /// snapshot of <see cref="OnlineLDA.CapturedClusters"/>, and syncs per-class colours
    /// from <see cref="OnlineLDA.ClusterColors"/>. No-ops when not in 3D mode.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the monitor now reflects the captured set (including the
    /// non-3D no-op); <see langword="false"/> when the rebuild failed, so the caller can retry.
    /// </returns>
    private bool RebuildFrozen3DPoints()
    {
        if (Scatter3D == null || !Is3D) return true;

        try
        {
            var allPts = new List<Point3D>();
            var allCls = new List<int>();

            var colorSnapshot = _onlineLda.ClusterColors
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // The data thread appends to CapturedClusters while capturing, so both the dictionary
            // and each point list are copied before they are walked
            var clusterSnapshot = _onlineLda.CapturedClusters
                .Select(kvp => (Label: kvp.Key, Points: kvp.Value.ToList())).ToList();

            foreach (var (label, hexColor) in colorSnapshot)
            {
                int cls = GetClassIndex(label);
                Scatter3D.SetClassColor(cls, SKColor.Parse(hexColor));
            }

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
            Console.WriteLine($"[OnlineLDAViewModel] Frozen 3D rebuild error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Rebuilds <see cref="ClusterInfoList"/> from the current <see cref="_labelPoints"/> dictionary.
    /// Skips the <c>"default"</c> (live-data) series and any empty collections.
    /// Also fires <see cref="HasClusters"/> change notification.
    /// </summary>
    private void UpdateClusterInfo()
    {
        ClusterInfoList.Clear();

        foreach (var (label, points) in _labelPoints)
        {
            if (label == "default") continue;
            if (points.Count == 0) continue;

            string colorHex;
            if (_onlineLda.ClusterColors.TryGetValue(label, out var c))
            {
                colorHex = c;
            }
            else
            {
                colorHex = "#808080";
            }

            ClusterInfoList.Add(new ClusterInfo
            {
                Label = label,
                PointCount = points.Count,
                ColorBrush = new SolidColorBrush(Color.Parse(colorHex))
            });
        }

        OnPropertyChanged(nameof(HasClusters));
    }

    #endregion

    #region Commands

    /// <summary>
    /// Removes a single named cluster from both the model and this ViewModel,
    /// then recalculates axis bounds from the remaining data.
    /// </summary>
    /// <param name="label">The cluster label to remove. No-op when <see langword="null"/> or empty.</param>
    [RelayCommand]
    private void ClearCluster(string? label)
    {
        if (string.IsNullOrEmpty(label)) return;

        Console.WriteLine($"[OnlineLDAViewModel] Clearing cluster '{label}'");

        _onlineLda.ClearCluster(label);

        if (_labelPoints.TryGetValue(label, out var points))
        {
            points.Clear();
            _labelPoints.Remove(label);
        }

        if (_labelSeries.TryGetValue(label, out var series))
        {
            _allSeries.Remove(series);
            _labelSeries.Remove(label);
        }

        ScatterSeries = _allSeries.ToArray();

        RecalculateBounds();

        _clusterInfoUpdatePending = true;
        _frozen3DUpdatePending = true;
    }

    /// <summary>
    /// Removes all clusters from the model and ViewModel, resets the class-index map, clears the
    /// <see cref="Scatter3DMonitor"/> entirely (live points as well as frozen ones) along with its
    /// class colours, resets axis bounds to their defaults, and clears the scatter chart.
    /// </summary>
    [RelayCommand]
    private void ClearAllClusters()
    {
        Console.WriteLine($"[OnlineLDAViewModel] Clearing all clusters");

        _onlineLda.ClearAllClusters();

        lock (_lock)
        {
            _clusterClassMap.Clear();
            _nextClassIndex = 0;
        }

        // Live points still carry the deleted labels' class indices, which the next capture reuses
        Scatter3D?.Clear();
        Scatter3D?.ClearClassColors();

        foreach (var points in _labelPoints.Values)
        {
            points.Clear();
        }
        _labelPoints.Clear();
        _labelSeries.Clear();
        _allSeries.Clear();

        ScatterSeries = Array.Empty<ISeries>();

        lock (_lock)
        {
            _minX = _minY = double.MaxValue;
            _maxX = _maxY = double.MinValue;
        }

        if (XAxes?.Length > 0)
        {
            XAxes[0].MinLimit = -5;
            XAxes[0].MaxLimit = 5;
        }
        if (YAxes?.Length > 0)
        {
            YAxes[0].MinLimit = -5;
            YAxes[0].MaxLimit = 5;
        }

        _clusterInfoUpdatePending = true;
        _frozen3DUpdatePending = true;
    }

    /// <summary>
    /// Recomputes <see cref="_minX"/>, <see cref="_maxX"/>, <see cref="_minY"/>,
    /// <see cref="_maxY"/> by scanning all remaining point collections.
    /// Called after a single cluster is removed to re-fit the axes to the surviving data.
    /// </summary>
    private void RecalculateBounds()
    {
        lock (_lock)
        {
            _minX = _minY = double.MaxValue;
            _maxX = _maxY = double.MinValue;

            foreach (var points in _labelPoints.Values)
            {
                foreach (var point in points)
                {
                    if (point.X.HasValue && point.Y.HasValue)
                    {
                        if (point.X.Value < _minX) _minX = point.X.Value;
                        if (point.X.Value > _maxX) _maxX = point.X.Value;
                        if (point.Y.Value < _minY) _minY = point.Y.Value;
                        if (point.Y.Value > _maxY) _maxY = point.Y.Value;
                    }
                }
            }
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Unsubscribes from the <see cref="VisualizationTimer"/> and the <see cref="OnlineLDA"/> model,
    /// and disposes the <see cref="Scatter3DMonitor"/> if one was created, so that no further timer
    /// ticks or subscriber callbacks reach this instance once it has been disposed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        VisualizationTimer.Instance.Unsubscribe(_timerCallback);
        _onlineLda?.RemoveSubscriber(this);

        if (_onlineLda != null)
            _onlineLda.PropertyChanged -= OnLdaPropertyChanged;

        Scatter3D?.Dispose();
    }

    #endregion
}
