using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.ViewModels.Analytics;

/// <summary>
/// ViewModel for the Supervised UMAP visualisation card.
/// </summary>
/// <remarks>
/// <para>
/// Bridges the <see cref="SupervisedUMAP"/> model and the Avalonia UI. Subscribes to the model
/// via <see cref="ISubscriber"/> and receives projected <c>Vector&lt;double&gt;</c> values
/// (after fitting) on a background thread. Points are buffered and consumed on the shared
/// <see cref="VisualizationTimer"/> tick.
/// </para>
/// <para>
/// <b>Key differences from <see cref="SupervisedPCAViewModel"/>:</b>
/// The scatter plot shows <see cref="SupervisedUMAP.TransformedClusters"/> (populated after fit),
/// not captured features directly. The live stream only appears after the model is fitted.
/// No variance chart — UMAP does not produce explained variance ratios.
/// </para>
/// </remarks>
public partial class SupervisedUMAPViewModel : ObservableObject, ISubscriber, IDisposable
{
    #region Fields

    private readonly SupervisedUMAP _umap;
    private readonly object _lock = new();
    private readonly Action _timerCallback;
    private bool _disposed;

    #endregion

    #region Live Stream State

    private readonly List<ObservablePoint> _liveScatterPoints = new();
    private readonly ObservableCollection<ObservablePoint> _liveScatterPointsUI = new();
    private readonly List<(double X, double Y)> _pointBuffer = new();

    #endregion

    #region Observable Properties

    [ObservableProperty] private int _maxScatterPoints = 1000;
    [ObservableProperty] private ISeries[] _scatterSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _xAxes;
    [ObservableProperty] private Axis[] _yAxes;
    [ObservableProperty] private string _chartTitle = "";
    [ObservableProperty] private string _sampleCountText = "0 samples";
    [ObservableProperty] private string _statusText = "No data captured";
    [ObservableProperty] private bool _is3D;
    [ObservableProperty] private string _labelInput = "";

    /// <summary>
    /// When <see langword="true"/>, the 3D projection pane is shown (only meaningful in 3-component
    /// mode). Toggled from the view; a guard keeps at least one of the 3D/2D panes visible.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Show3DPane))]
    private bool _show3DView = true;

    /// <summary>When <see langword="true"/>, the 2D (UMAP1 vs UMAP2) scatter pane is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Show2DPane))]
    private bool _show2DView = true;

    #endregion

    #region 3D Scatter

    /// <summary>
    /// Optional Three.js-backed 3D scatter monitor. Created only in 3-component mode.
    /// </summary>
    public Scatter3DMonitor? Scatter3D { get; }

    #endregion

    #region Nested Types

    public class ClusterInfo
    {
        public string Label { get; set; } = "";
        public int PointCount { get; set; }
        public IBrush? ColorBrush { get; set; }
    }

    #endregion

    #region Public Surface

    public ObservableCollection<ClusterInfo> ClusterInfoList { get; } = new();
    public bool HasClusters => _umap?.CapturedFeatures?.Count > 0;
    public bool HasTransformedClusters => _umap?.TransformedClusters?.Count > 0;
    public SupervisedUMAP SupervisedUMAP => _umap;

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

    private double _minX = -15, _maxX = 15, _minY = -15, _maxY = 15;
    private bool _axisBoundsDirty = true;
    private const double AxisMarginFactor = 0.25;

    #endregion

    #region Pending-Update Flags

    private bool _clusterUpdatePending;
    private DateTime _lastTextUpdate = DateTime.MinValue;
    private DateTime _lastClusterUpdate = DateTime.MinValue;

    #endregion

    #region 3D Class Mapping

    private readonly Dictionary<string, int> _clusterClassMap = new();
    private int _nextClassIndex;

    #endregion

    #region Constructor

    public SupervisedUMAPViewModel(SupervisedUMAP umap)
    {
        _umap = umap ?? throw new ArgumentNullException(nameof(umap));
        _is3D = umap.NComponents == 3;
        _chartTitle = _is3D ? "UMAP 3D" : "UMAP 2D";

        _timerCallback = OnTimerTick;

        if (_is3D)
        {
            Scatter3D = new Scatter3DMonitor(maxPoints: 1000)
            {
                XLabel = "UMAP1",
                YLabel = "UMAP2",
                ZLabel = "UMAP3",
            };
        }

        InitializeAxes();
        InitializeSeries();

        _umap.AddSubscriber(this);
        _umap.PropertyChanged += OnUmapPropertyChanged;

        if (_umap.TransformedClusters.Count > 0)
            _clusterUpdatePending = true;

        VisualizationTimer.Instance.Subscribe(_timerCallback);
    }

    #endregion

    #region Initialisation

    private void InitializeAxes()
    {
        // UMAP embedding ranges are typically wider than PCA
        XAxes = new Axis[]
        {
            new Axis
            {
                Name = "UMAP1", NameTextSize = 12,
                NamePaint = new SolidColorPaint(SKColors.WhiteSmoke),
                LabelsPaint = new SolidColorPaint(SKColors.Gray),
                SeparatorsPaint = new SolidColorPaint(new SKColor(64, 64, 64)),
                MinLimit = -15, MaxLimit = 15
            }
        };
        YAxes = new Axis[]
        {
            new Axis
            {
                Name = "UMAP2", NameTextSize = 12,
                NamePaint = new SolidColorPaint(SKColors.WhiteSmoke),
                LabelsPaint = new SolidColorPaint(SKColors.Gray),
                SeparatorsPaint = new SolidColorPaint(new SKColor(64, 64, 64)),
                MinLimit = -15, MaxLimit = 15
            }
        };
    }

    private void InitializeSeries()
    {
        ScatterSeries = new ISeries[]
        {
            new ScatterSeries<ObservablePoint>
            {
                Values = _liveScatterPointsUI,
                GeometrySize = 7,
                Fill = new SolidColorPaint(new SKColor(0, 191, 255, 200)),
                Stroke = new SolidColorPaint(new SKColor(255, 255, 255, 120), 1.5f),
                Name = "Live"
            }
        };
    }

    #endregion

    #region Commands

    [RelayCommand]
    private void StartCapture()
    {
        if (!string.IsNullOrWhiteSpace(LabelInput))
        {
            _umap.StartCapture(LabelInput);
            UpdateStatusText();
            _clusterUpdatePending = true;
        }
    }

    [RelayCommand]
    private void StopCapture()
    {
        _umap.StopCapture();
        UpdateStatusText();
        _clusterUpdatePending = true;
    }

    [RelayCommand]
    private async Task FitModelAsync()
    {
        if (_umap.CapturedFeatures.Count == 0) return;

        StatusText = "Fitting UMAP...";

        bool success = await Task.Run(() => _umap.FitModel());

        if (success)
        {
            StatusText = $"Fitted — {_umap.TotalCalibrationSamples} points, {_umap.CapturedFeatures.Count} classes";
            _clusterUpdatePending = true;
            _axisBoundsDirty = true;

            // Reset axis bounds from transformed cluster data
            RecomputeAxisBoundsFromClusters();
        }
        else
        {
            StatusText = "Fit failed — check console";
        }

        OnPropertyChanged(nameof(HasTransformedClusters));
    }

    [RelayCommand]
    private void ClearCluster(string label)
    {
        _umap.ClearCluster(label);
        UpdateStatusText();
        _clusterUpdatePending = true;
        OnPropertyChanged(nameof(HasClusters));
    }

    [RelayCommand]
    private void ClearAllClusters()
    {
        _umap.ClearAllClusters();
        _liveScatterPointsUI.Clear();
        lock (_lock) { _liveScatterPoints.Clear(); _pointBuffer.Clear(); }
        // The 2D live stream is dropped just above, so the 3D live ring goes with it
        Scatter3D?.Clear();
        Scatter3D?.ClearClassColors();
        lock (_lock) { _minX = _minY = -15; _maxX = _maxY = 15; }
        _axisBoundsDirty = true;
        _clusterUpdatePending = true;
        UpdateStatusText();
        OnPropertyChanged(nameof(HasClusters));
        OnPropertyChanged(nameof(HasTransformedClusters));
    }

    #endregion

    #region Model Event Handlers

    private void OnUmapPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed) return;

        switch (e.PropertyName)
        {
            case nameof(SupervisedUMAP.CapturedFeatures):
            case nameof(SupervisedUMAP.TransformedClusters):
                _clusterUpdatePending = true;
                break;
        }
    }

    #endregion

    #region Property Callbacks

    /// <summary>
    /// Keeps at least one projection pane visible when the user toggles 3D off, and pauses or
    /// resumes the 3D renderer so it stops drawing frames while the pane is hidden.
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

    partial void OnMaxScatterPointsChanged(int value)
    {
        lock (_lock)
        {
            while (_liveScatterPoints.Count > value)
                _liveScatterPoints.RemoveAt(0);
        }
        while (_liveScatterPointsUI.Count > value)
            _liveScatterPointsUI.RemoveAt(0);

        if (Scatter3D != null)
            Scatter3D.MaxPoints = value;

        _axisBoundsDirty = true;
    }

    #endregion

    #region Timer Callback

    private void OnTimerTick()
    {
        if (_disposed) return;
        try
        {
            ProcessPointBuffer();
            if (_axisBoundsDirty) { UpdateAxisBoundsUI(); _axisBoundsDirty = false; }

            if (_clusterUpdatePending)
            {
                bool shouldRebuild = true;
                if (_umap.IsCapturing)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastClusterUpdate).TotalMilliseconds < 500)
                        shouldRebuild = false;
                    else
                        _lastClusterUpdate = now;
                }

                if (shouldRebuild)
                {
                    RebuildAllSeriesUi();
                    if (RebuildFrozen3DPoints())
                        _clusterUpdatePending = false;
                    OnPropertyChanged(nameof(HasClusters));
                    OnPropertyChanged(nameof(HasTransformedClusters));
                }
            }

            // Throttled text updates
            var now2 = DateTime.UtcNow;
            if ((now2 - _lastTextUpdate).TotalMilliseconds > 200)
            {
                _lastTextUpdate = now2;
                UpdateStatusText();
                SampleCountText = _umap.IsFitted
                    ? $"{_umap.TransformCount:N0} transforms"
                    : $"{_umap.TotalCalibrationSamples:N0} captured";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SupervisedUMAPViewModel] Timer error: {ex.Message}");
        }
    }

    #endregion

    #region Point Processing

    private void ProcessPointBuffer()
    {
        List<(double X, double Y)> points;
        lock (_lock)
        {
            if (_pointBuffer.Count == 0) return;
            points = new List<(double, double)>(_pointBuffer);
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
    /// Receives a projected embedding vector published by <see cref="SupervisedUMAP"/>.
    /// Called on a background thread after the model is fitted.
    /// </summary>
    public void ReceiveInput(object sender, object value)
    {
        if (_disposed) return;
        if (value is not Vector projection) return;
        if (projection.Count < 2) return;

        double x = projection[0];
        double y = projection[1];

        if (_is3D && projection.Count >= 3)
        {
            Scatter3D?.AddPoint(x, y, projection[2], 0);
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
    }

    #endregion

    #region Chart Rebuild

    /// <summary>
    /// Rebuilds the frozen cluster point sets in the <see cref="Scatter3DMonitor"/> from a
    /// snapshot of the model's transformed clusters, and syncs per-class colours. No-ops when
    /// not in 3D mode.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the monitor now reflects the transformed set (including the
    /// non-3D no-op); <see langword="false"/> when the rebuild failed, so the caller can retry.
    /// </returns>
    private bool RebuildFrozen3DPoints()
    {
        if (Scatter3D == null || !_is3D) return true;

        try
        {
            var allPts = new List<Point3D>();
            var allCls = new List<int>();

            var colorSnapshot = _umap.ClusterColors
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            foreach (var (label, hexColor) in colorSnapshot)
            {
                int cls = GetClassIndex(label);
                Scatter3D.SetClassColor(cls, SKColor.Parse(hexColor));
            }

            // Use TransformedClusters (post-fit) if available, otherwise nothing to show.
            // A fit repopulates it off the UI thread, so dictionary and lists are copied first
            var source = _umap.TransformedClusters
                .Select(kvp => (Label: kvp.Key, Points: kvp.Value.ToList())).ToList();
            foreach (var (label, points) in source)
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
            Console.WriteLine($"[SupervisedUMAPViewModel] Frozen 3D rebuild error: {ex.Message}");
            return false;
        }
    }

    private void RebuildAllSeriesUi()
    {
        var seriesList = new List<ISeries>();
        ClusterInfoList.Clear();

        try
        {
            // Show TransformedClusters if fitted, otherwise show capture counts in legend only
            var displaySource = _umap.IsFitted
                ? _umap.TransformedClusters
                : new Dictionary<string, List<Vector>>();

            var colorSnapshot = _umap.ClusterColors
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            foreach (var (label, _) in _umap.CapturedFeatures)
            {
                var color = colorSnapshot.TryGetValue(label, out var c) ? c : "#808080";
                var skColor = SKColor.Parse(color);
                int rawCount = _umap.CapturedFeatures.TryGetValue(label, out var raw) ? raw.Count : 0;

                // Only add scatter points if we have transformed data
                if (displaySource.TryGetValue(label, out var points) && points.Count > 0)
                {
                    var clusterPoints = new ObservableCollection<ObservablePoint>(
                        points.Where(p => p.Count >= 2).Select(p => new ObservablePoint(p[0], p[1]))
                    );

                    seriesList.Add(new ScatterSeries<ObservablePoint>
                    {
                        Values = clusterPoints,
                        GeometrySize = 8,
                        Fill = new SolidColorPaint(new SKColor(skColor.Red, skColor.Green, skColor.Blue, 100)),
                        Stroke = new SolidColorPaint(skColor, 1.5f),
                        Name = label
                    });
                }

                ClusterInfoList.Add(new ClusterInfo
                {
                    Label = label,
                    PointCount = rawCount,
                    ColorBrush = new SolidColorBrush(Color.Parse(color))
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SupervisedUMAPViewModel] Cluster series error: {ex.Message}");
        }

        // Live stream series on top
        seriesList.Add(new ScatterSeries<ObservablePoint>
        {
            Values = _liveScatterPointsUI,
            GeometrySize = 7,
            Fill = new SolidColorPaint(new SKColor(0, 191, 255, 200)),
            Stroke = new SolidColorPaint(new SKColor(255, 255, 255, 120), 1.5f),
            Name = "Live"
        });

        ScatterSeries = seriesList.ToArray();
    }

    #endregion

    #region Axis Helpers

    private void UpdateAxisBoundsUI()
    {
        double minX, maxX, minY, maxY;
        lock (_lock) { minX = _minX; maxX = _maxX; minY = _minY; maxY = _maxY; }
        if (minX >= maxX || minY >= maxY) return;

        double rangeX = Math.Max(maxX - minX, 0.1);
        double rangeY = Math.Max(maxY - minY, 0.1);
        double maxRange = Math.Max(rangeX, rangeY);
        double margin = maxRange * AxisMarginFactor;
        double half = (maxRange + 2 * margin) / 2;
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;

        if (_xAxes?.Length > 0) { _xAxes[0].MinLimit = cx - half; _xAxes[0].MaxLimit = cx + half; }
        if (_yAxes?.Length > 0) { _yAxes[0].MinLimit = cy - half; _yAxes[0].MaxLimit = cy + half; }
    }

    private void RecomputeAxisBoundsFromClusters()
    {
        lock (_lock)
        {
            _minX = double.MaxValue; _maxX = double.MinValue;
            _minY = double.MaxValue; _maxY = double.MinValue;

            foreach (var (_, points) in _umap.TransformedClusters)
            {
                foreach (var p in points)
                {
                    if (p.Count < 2) continue;
                    if (p[0] < _minX) _minX = p[0];
                    if (p[0] > _maxX) _maxX = p[0];
                    if (p[1] < _minY) _minY = p[1];
                    if (p[1] > _maxY) _maxY = p[1];
                }
            }

            if (_minX >= _maxX) { _minX = -15; _maxX = 15; }
            if (_minY >= _maxY) { _minY = -15; _maxY = 15; }
        }
        _axisBoundsDirty = true;
    }

    #endregion

    #region Status Helpers

    private void UpdateStatusText()
    {
        if (_umap.IsFitting)
        {
            StatusText = "Fitting...";
        }
        else if (_umap.IsFitted)
        {
            StatusText = $"Fitted — {_umap.TransformCount:N0} transforms";
        }
        else if (_umap.IsCapturing)
        {
            StatusText = $"Capturing '{_umap.CurrentLabel}' — {_umap.CurrentCaptureCount} pts";
        }
        else if (_umap.CapturedFeatures.Count > 0)
        {
            int total = _umap.CapturedFeatures.Sum(kvp => kvp.Value.Count);
            StatusText = $"{_umap.CapturedFeatures.Count} classes, {total} points — ready to fit";
        }
        else
        {
            StatusText = "No data captured";
        }
    }

    /// <remarks>
    /// Called from both the UI thread (via <see cref="RebuildFrozen3DPoints"/>) and the data
    /// thread, so the lookup and the counter increment are taken together under
    /// <see cref="_lock"/>; neither caller holds the lock already.
    /// </remarks>
    private int GetClassIndex(string label)
    {
        lock (_lock)
        {
            if (_clusterClassMap.TryGetValue(label, out int idx)) return idx;
            idx = ++_nextClassIndex; // 0 reserved for live stream
            _clusterClassMap[label] = idx;
            return idx;
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        VisualizationTimer.Instance.Unsubscribe(_timerCallback);
        if (_umap != null)
        {
            _umap.RemoveSubscriber(this);
            _umap.PropertyChanged -= OnUmapPropertyChanged;
        }
        Scatter3D?.Dispose();
    }

    #endregion
}