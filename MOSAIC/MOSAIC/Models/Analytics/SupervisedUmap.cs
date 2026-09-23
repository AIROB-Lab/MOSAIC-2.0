using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Manager.Python;
using MOSAIC.Diagnostics;
using Python.Runtime;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Models.Analytics;

/// <example>
/// <para>Block entry for a larger pipeline. Params: embedding components, neighbour count, minimum distance, random seed. Requires the Python UMAP environment. Capture labelled clusters and fit before interpreting transformed output.</para>
/// <code language="json">
/// {
///   "SupervisedUMAP": {
///     "Type": "umap",
///     "Inputs": ["Features"],
///     "Params": [3, 15, 0.1, 42]
///   }
/// }
/// </code>
/// </example>
public partial class SupervisedUMAP : BaseBlock
{
    #region Fields

    private dynamic? _np;
    private dynamic? _umapModel;

    /// <summary>
    /// Latches the transform failure below, which recurs on every sample once Python throws, so it
    /// is reported once per fitted run. Cleared wherever the model becomes fitted.
    /// </summary>
    private bool _transformErrorReported;

    #endregion

    #region UMAP Parameters

    [ObservableProperty] private int _nComponents = 3;
    [ObservableProperty] private int _nNeighbors = 15;
    [ObservableProperty] private double _minDist = 0.1;
    [ObservableProperty] private int _randomState = 42;
    [ObservableProperty] private string _metric = "euclidean";

    #endregion

    #region Cluster Storage

    public Dictionary<string, List<Vector>> CapturedFeatures { get; } = new();
    public Dictionary<string, List<Vector>> TransformedClusters { get; } = new();
    public Dictionary<string, string> ClusterColors { get; } = new();

    private readonly List<string> _availableColors = new()
    {
        "#FF6B6B", "#4ECDC4", "#45B7D1", "#FFA07A", "#98D8C8", "#F7DC6F",
        "#BB8FCE", "#85C1E2", "#F8B88B", "#AAB7B8", "#52C7B8", "#FFB6C1",
    };

    private int _colorIndex;

    #endregion

    #region Observable Properties

    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string _currentLabel = "";
    [ObservableProperty] private int _currentCaptureCount;
    [ObservableProperty] private bool _isFitted;
    [ObservableProperty] private bool _isFitting;
    [ObservableProperty] private int _totalCalibrationSamples;
    [ObservableProperty] private long _transformCount;
    [ObservableProperty] private int _inputDimension;

    #endregion

    #region Constructor

    public SupervisedUMAP(
        string name,
        double desiredRate = 0,
        int nComponents = 3,
        int nNeighbors = 15,
        double minDist = 0.1,
        int randomState = 42)
        : base(name, desiredRate)
    {
        if (nComponents < 2 || nComponents > 3)
            throw new ArgumentOutOfRangeException(nameof(nComponents), "n_components must be 2 or 3 for visualization");

        _nComponents = nComponents;
        _nNeighbors = nNeighbors;
        _minDist = minDist;
        _randomState = randomState;

        InitializePython();
        Console.WriteLine($"[SupervisedUMAP '{Name}'] Initialized (n_components={nComponents}, n_neighbors={nNeighbors}, min_dist={minDist})");
    }

    #endregion

    #region Factory Method

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_umap.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_umap";

    public static SupervisedUMAP ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "SupervisedUMAP";
        var rate = m.DesiredRate ?? 0;

        int nComp = TryInt(m.Params, 0, 3);
        int nNeigh = TryInt(m.Params, 1, 15);
        double minD = TryDouble(m.Params, 2, 0.1);
        int seed = TryInt(m.Params, 3, 42);

        var block = new SupervisedUMAP(name, rate, nComp, nNeigh, minD, seed);
        return block;
    }

    #endregion

    #region Python Initialization

    private void InitializePython()
    {
        try
        {
            var configPath = PythonNetManager.ResolveConfigPath("config.json");
            Console.WriteLine($"[SupervisedUMAP '{Name}'] Python config: {configPath}");
            PythonNetManager.Initialize(configPath);

            using (Py.GIL())
            {
                _np = Py.Import("numpy");
                Console.WriteLine($"[SupervisedUMAP '{Name}'] numpy imported");

                // Under the Scripts folder the config already put on sys.path, so this relocates
                // with the rest of the deployment instead of assuming a source checkout.
                var scriptDir = System.IO.Path.Combine(PythonNetManager.Instance.ScriptsPath, "Umap");
                Console.WriteLine($"[SupervisedUMAP '{Name}'] Script dir: {scriptDir} (exists={System.IO.Directory.Exists(scriptDir)})");
                dynamic sys = Py.Import("sys");
                sys.path.append(scriptDir);

                dynamic bridge = Py.Import("umap_bridge");
                Console.WriteLine($"[SupervisedUMAP '{Name}'] umap_bridge imported");

                _umapModel = bridge.UmapModel(
                    n_components: NComponents,
                    n_neighbors: NNeighbors,
                    min_dist: MinDist,
                    metric: Metric,
                    random_state: RandomState
                );
                Console.WriteLine($"[SupervisedUMAP '{Name}'] UmapModel created");
            }
        }
        catch (Exception ex)
        {
            Log.Error("SupervisedUMAP", Name, ex, "Python init failed.");
        }
    }

    public void RecreateModel()
    {
        try
        {
            using (Py.GIL())
            {
                dynamic bridge = Py.Import("umap_bridge");
                _umapModel = bridge.UmapModel(
                    n_components: NComponents,
                    n_neighbors: NNeighbors,
                    min_dist: MinDist,
                    metric: Metric,
                    random_state: RandomState
                );
            }

            IsFitted = false;
            TransformedClusters.Clear();
            TransformCount = 0;
            Console.WriteLine($"[SupervisedUMAP '{Name}'] Model recreated");
        }
        catch (Exception ex)
        {
            Log.Error("SupervisedUMAP", Name, ex, "RecreateModel error.");
        }
    }

    #endregion

    #region Data Reception

    protected override void OnReceive(object sender, object data)
    {
        if (data is null) return;

        switch (data)
        {
            case Vector v:
                ProcessVector(v);
                return;
            case Matrix M:
                for (int r = 0; r < M.RowCount; r++)
                    ProcessVector(M.Row(r));
                return;
        }
    }

    private void ProcessVector(Vector input)
    {
        if (input.Count == 0) return;

        if (InputDimension == 0)
            InputDimension = input.Count;

        if (IsCapturing && !string.IsNullOrEmpty(CurrentLabel))
        {
            if (CapturedFeatures.TryGetValue(CurrentLabel, out var list))
            {
                list.Add(input.Clone());
                CurrentCaptureCount++;

                if (CurrentCaptureCount % 50 == 0)
                    Console.WriteLine($"[SupervisedUMAP '{Name}'] Captured {CurrentCaptureCount} for '{CurrentLabel}'");

                if (CurrentCaptureCount % 10 == 0)
                    OnPropertyChanged(nameof(CurrentCaptureCount));
            }
        }

        if (IsFitted && !IsFitting && _umapModel != null)
        {
            try
            {
                Vector projection;
                using (Py.GIL())
                {
                    dynamic inputNp = _np!.array(input.ToArray(), dtype: _np.float64);
                    dynamic result = _umapModel.transform(inputNp);
                    var flat = new double[NComponents];
                    for (int i = 0; i < NComponents; i++)
                        flat[i] = (double)result[0][i];
                    projection = Vector.Build.DenseOfArray(flat);
                }

                TransformCount++;
                PublishProjection(projection);
            }
            catch (Exception ex)
            {
                if (!_transformErrorReported)
                {
                    _transformErrorReported = true;
                    Log.Error("SupervisedUMAP", Name, ex, "Transform error; further failures in this run are not reported.");
                }
            }
        }
    }

    protected virtual void PublishProjection(Vector projection)
    {
        Publish(projection);
    }

    #endregion

    #region Capture Control

    public void StartCapture(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            Console.WriteLine($"[SupervisedUMAP '{Name}'] Cannot start capture with empty label");
            return;
        }

        if (IsCapturing)
        {
            Console.WriteLine($"[SupervisedUMAP '{Name}'] Already capturing '{CurrentLabel}', stopping first");
            StopCapture();
        }

        IsCapturing = true;
        CurrentLabel = label.Trim();
        CurrentCaptureCount = 0;

        if (!CapturedFeatures.ContainsKey(CurrentLabel))
        {
            CapturedFeatures[CurrentLabel] = new List<Vector>();
            ClusterColors[CurrentLabel] = _availableColors[_colorIndex % _availableColors.Count];
            _colorIndex++;
            Console.WriteLine($"[SupervisedUMAP '{Name}'] Started new cluster '{CurrentLabel}' (color {ClusterColors[CurrentLabel]})");
        }
        else
        {
            Console.WriteLine($"[SupervisedUMAP '{Name}'] Resuming capture for '{CurrentLabel}' ({CapturedFeatures[CurrentLabel].Count} existing)");
        }

        OnPropertyChanged(nameof(CapturedFeatures));
    }

    public void StopCapture()
    {
        if (!IsCapturing)
        {
            Console.WriteLine($"[SupervisedUMAP '{Name}'] Not currently capturing");
            return;
        }

        var count = CapturedFeatures.TryGetValue(CurrentLabel, out var pts) ? pts.Count : 0;
        Console.WriteLine($"[SupervisedUMAP '{Name}'] Stopped capturing '{CurrentLabel}'. Total: {count} (session: {CurrentCaptureCount})");

        IsCapturing = false;
        CurrentLabel = "";
        CurrentCaptureCount = 0;

        UpdateTotalCalibrationSamples();
        OnPropertyChanged(nameof(CapturedFeatures));
    }

    public void ClearCluster(string label)
    {
        if (CapturedFeatures.Remove(label))
        {
            TransformedClusters.Remove(label);
            ClusterColors.Remove(label);
            Console.WriteLine($"[SupervisedUMAP '{Name}'] Cleared cluster '{label}'");
            UpdateTotalCalibrationSamples();
            OnPropertyChanged(nameof(CapturedFeatures));
        }
    }

    public void ClearAllClusters()
    {
        CapturedFeatures.Clear();
        TransformedClusters.Clear();
        ClusterColors.Clear();
        _colorIndex = 0;
        IsFitted = false;
        TransformCount = 0;
        TotalCalibrationSamples = 0;
        Console.WriteLine($"[SupervisedUMAP '{Name}'] Cleared all clusters");
        OnPropertyChanged(nameof(CapturedFeatures));
    }

    private void UpdateTotalCalibrationSamples()
    {
        TotalCalibrationSamples = CapturedFeatures.Sum(kvp => kvp.Value.Count);
    }

    #endregion

    #region Fit

    public bool FitModel()
    {
        if (CapturedFeatures.Count == 0 || _umapModel == null)
        {
            Log.Warn("SupervisedUMAP", Name, $"Cannot fit: data={CapturedFeatures.Count} classes, model={(_umapModel != null ? "ok" : "NULL")}");
            return false;
        }

        int totalPoints = CapturedFeatures.Sum(kvp => kvp.Value.Count);
        if (totalPoints < NNeighbors)
        {
            Log.Warn("SupervisedUMAP", Name, $"Cannot fit: {totalPoints} points < n_neighbors={NNeighbors}");
            return false;
        }

        IsFitting = true;

        try
        {
            var labelOrder = CapturedFeatures.Keys.ToList();
            var labelToIndex = new Dictionary<string, int>();
            for (int i = 0; i < labelOrder.Count; i++)
                labelToIndex[labelOrder[i]] = i;

            int dim = CapturedFeatures.Values.First().First().Count;
            var allFeatures = new List<double[]>();
            var allLabels = new List<int>();

            foreach (var (label, vectors) in CapturedFeatures)
            {
                int classIdx = labelToIndex[label];
                foreach (var v in vectors)
                {
                    allFeatures.Add(v.ToArray());
                    allLabels.Add(classIdx);
                }
            }

            double[,] embeddingResult;
            using (Py.GIL())
            {
                int n = allFeatures.Count;
                var flatArray = new double[n, dim];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < dim; j++)
                        flatArray[i, j] = allFeatures[i][j];

                dynamic xNp = _np!.array(flatArray, dtype: _np.float64);
                dynamic yNp = _np.array(allLabels.ToArray(), dtype: _np.int64);

                var sw = Stopwatch.StartNew();
                dynamic embedding = _umapModel.fit(xNp, yNp);
                sw.Stop();

                Console.WriteLine($"[SupervisedUMAP '{Name}'] Fit complete: {n} points, {dim}D -> {NComponents}D in {sw.ElapsedMilliseconds}ms");

                int rows = (int)embedding.shape[0];
                int cols = (int)embedding.shape[1];
                embeddingResult = new double[rows, cols];
                for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    embeddingResult[i, j] = (double)embedding[i][j];
            }

            TransformedClusters.Clear();
            int idx = 0;
            foreach (var (label, vectors) in CapturedFeatures)
            {
                var transformedList = new List<Vector>();
                for (int i = 0; i < vectors.Count; i++)
                {
                    var coords = new double[NComponents];
                    for (int c = 0; c < NComponents; c++)
                        coords[c] = embeddingResult[idx, c];
                    transformedList.Add(Vector.Build.DenseOfArray(coords));
                    idx++;
                }
                TransformedClusters[label] = transformedList;
            }

            IsFitted = true;
            TransformCount = 0;
            _transformErrorReported = false;

            OnPropertyChanged(nameof(TransformedClusters));
            Console.WriteLine($"[SupervisedUMAP '{Name}'] Ready for online transform");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("SupervisedUMAP", Name, ex, "Fit error.");
            return false;
        }
        finally
        {
            IsFitting = false;
        }
    }

    #endregion

    #region Model Persistence

    public void SaveModel(string path)
    {
        if (!IsFitted || _umapModel == null) return;
        try
        {
            using (Py.GIL()) { _umapModel.save(path); }
            Console.WriteLine($"[SupervisedUMAP '{Name}'] Model saved to {path}");
        }
        catch (Exception ex)
        {
            Log.Error("SupervisedUMAP", Name, ex, "Save error.");
        }
    }

    public void LoadModel(string path)
    {
        if (_umapModel == null) return;
        try
        {
            using (Py.GIL())
            {
                _umapModel.load(path);
                dynamic trainEmb = _umapModel.get_train_embedding();
                dynamic trainLabels = _umapModel.get_train_labels();
                var embedding = (double[,])trainEmb;
                int[] labels = trainLabels != null ? (int[])trainLabels : Array.Empty<int>();

                TransformedClusters.Clear();
                var labelGroups = new Dictionary<int, List<Vector>>();
                for (int i = 0; i < embedding.GetLength(0); i++)
                {
                    int lbl = i < labels.Length ? labels[i] : 0;
                    if (!labelGroups.ContainsKey(lbl))
                        labelGroups[lbl] = new List<Vector>();
                    var coords = new double[NComponents];
                    for (int c = 0; c < NComponents; c++)
                        coords[c] = embedding[i, c];
                    labelGroups[lbl].Add(Vector.Build.DenseOfArray(coords));
                }

                foreach (var (lbl, pts) in labelGroups)
                {
                    string labelName = $"class_{lbl}";
                    TransformedClusters[labelName] = pts;
                    if (!ClusterColors.ContainsKey(labelName))
                    {
                        ClusterColors[labelName] = _availableColors[_colorIndex % _availableColors.Count];
                        _colorIndex++;
                    }
                }
            }

            IsFitted = true;
            TransformCount = 0;
            _transformErrorReported = false;
            OnPropertyChanged(nameof(TransformedClusters));
            Console.WriteLine($"[SupervisedUMAP '{Name}'] Model loaded from {path}");
        }
        catch (Exception ex)
        {
            Log.Error("SupervisedUMAP", Name, ex, "Load error.");
        }
    }

    #endregion

    #region Reset

    public void Reset(bool preserveCaptures = true)
    {
        IsFitted = false;
        IsFitting = false;
        TransformCount = 0;
        TransformedClusters.Clear();

        if (!preserveCaptures)
        {
            CapturedFeatures.Clear();
            ClusterColors.Clear();
            _colorIndex = 0;
            TotalCalibrationSamples = 0;
        }

        RecreateModel();
        OnPropertyChanged(nameof(CapturedFeatures));
        OnPropertyChanged(nameof(TransformedClusters));
    }

    #endregion

    #region Dispose

    public override void Dispose()
    {
        using (Py.GIL())
        {
            _umapModel?.Dispose();
            _np?.Dispose();
        }
        _umapModel = null;
        _np = null;
        base.Dispose();
    }

    #endregion

    #region JSON Export

    protected override string JsonTypeName => "SupervisedUMAP";

    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object> { NComponents, NNeighbors, MinDist, RandomState };

    #endregion

    #region Helper Methods

    private static int TryInt(IReadOnlyList<object>? p, int idx, int def)
    {
        if (p == null || idx >= p.Count) return def;
        return p[idx] switch
        {
            int i => i,
            double d => (int)Math.Round(d),
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
            JsonElement je when je.ValueKind == JsonValueKind.String
                              && int.TryParse(je.GetString(), out var v) => v,
            _ => def
        };
    }

    private static double TryDouble(IReadOnlyList<object>? p, int idx, double def)
    {
        if (p == null || idx >= p.Count) return def;
        return p[idx] switch
        {
            double d => d,
            float f => f,
            int i => i,
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble(),
            JsonElement je when je.ValueKind == JsonValueKind.String
                              && double.TryParse(je.GetString(), NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out var v) => v,
            _ => def
        };
    }

    #endregion
}