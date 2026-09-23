using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Manager.Python;
using MOSAIC.Models.FlowControl;
using MOSAIC.Visualization;
using Python.Runtime;
using static MOSAIC.Components.Basics.JsonModel;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Models.MachineLearning;

/// <example>
/// <para>Block entry for a larger pipeline. Params: importable Python module name, class count, random seed, model save path. my_classifier is a placeholder for your module implementing the required Model interface. TrainingBuffer must be a TriggerBuffer. Configure Python and train before predicting.</para>
/// <code language="json">
/// {
///   "PyPredictor": {
///     "Type": "pypredictor",
///     "Inputs": ["Features", "TrainingBuffer"],
///     "Params": ["my_classifier", 2, 42, ""]
///   }
/// }
/// </code>
/// </example>
public sealed partial class PyPredictor : BaseBlock
{
    /// <summary>Needs exactly two inputs (e.g. a data stream + a Trigger/TriggerBuffer).</summary>
    public override int MinInputs => 2;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 2;

    #region Fields

    private dynamic? _np;
    private dynamic? _model;
    private readonly string _savePath = string.Empty;

    #endregion

    #region Observable Properties

    [ObservableProperty] private string _modulePath       = string.Empty;
    [ObservableProperty] private int    _numClasses       = 1;
    [ObservableProperty] private int    _randomSeed       = 42;
    [ObservableProperty] private bool   _isTraining;
    [ObservableProperty] private bool   _isTrained;
    [ObservableProperty] private long   _predictionCount;
    [ObservableProperty] private string _modelDescription = string.Empty;

    #endregion

    #region Public Surface

    public BlockVisualization Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    public event Action<Vector>? OnPrediction;
    public event Action<bool>?   OnTrainingStateChanged;
    public event Action?         OnBufferSegmentAdded;

    public TriggerBuffer? Buffer { get; set; }
    public string SavePath => _savePath;

    #endregion

    #region Constructor & Factory

    public PyPredictor(
        string name,
        double desiredRate,
        string modulePath,
        int    numClasses,
        int    randomSeed = 42,
        string savePath   = "")
        : base(name, desiredRate)
    {
        _modulePath = modulePath;
        _numClasses = numClasses;
        _randomSeed = randomSeed;
        _savePath   = savePath;

        UpdateDescription();

        PythonNetManager.Initialize(PythonNetManager.ResolveConfigPath("config.json"));

        using (Py.GIL())
        {
            _np = Py.Import("numpy");
            dynamic godFile = Py.Import(ModulePath);
            _model = godFile.Model(num_classes: NumClasses, random_seed: RandomSeed);
        }

        Debug.WriteLine($"[{Name}] Model loaded: {ModelDescription}");
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_predictions.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_predictions";

    public static PyPredictor ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        if (m.Params is null || m.Params.Count < 2)
            throw new ArgumentException(
                $"[PyPredictor] '{m.Name}' requires Params[0]=modulePath, Params[1]=numClasses.");

        var modulePath = GetString(m.Params[0], string.Empty);
        var numClasses = GetInt(m.Params[1], 1);
        var randomSeed = m.Params.Count > 2 ? GetInt(m.Params[2], 42) : 42;
        var savePath   = m.Params.Count > 3 ? GetString(m.Params[3], string.Empty) : string.Empty;
        var rate       = m.DesiredRate ?? 200;

        var block = ActivatorUtilities.CreateInstance<PyPredictor>(
            sp, m.Name, rate, modulePath, numClasses, randomSeed, savePath);

        return block;
    }

    #endregion

    #region JSON Export

    protected override string JsonTypeName => "PyPredictor";

    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object> { ModulePath, NumClasses, RandomSeed, _savePath };

    #endregion

    #region Data Pipeline

    protected override void OnReceive(object sender, object data)
    {
        if (sender is TriggerBuffer tb)
        {
            Buffer = tb;
            OnBufferSegmentAdded?.Invoke();
            return;
        }

        if (IsTraining || _model is null) return;

        try
        {
            if (data is Matrix matrix)
                Predict(matrix);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Predict error: {ex.Message}");
        }
    }

    private void Predict(Matrix inputMatrix)
    {
        Vector result;

        using (Py.GIL())
        {
            dynamic inputNp    = _np!.array(inputMatrix.ToArray(), dtype: _np.float32);
            dynamic prediction = _model!.predict(inputNp);
            dynamic probsNp    = prediction[1].astype(_np.float64);
            double[] probs     = probsNp.reshape(-1).As<double[]>();
            result             = Vector.Build.DenseOfArray(probs);
        }

        PredictionCount++;
        Publish(result);
        OnPrediction?.Invoke(result);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Viz.Feed(result),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    #endregion

    #region Training

    public void TrainIncremental(
        List<(string Label, Matrix Targets, Matrix Data)> clusters,
        int     epochs          = 30,
        bool    useDistillation = true,
        double  replayRatio     = 0.5,
        bool    jointFinetune   = true,
        int     jointEpochs     = 20,
        string? savePath        = null)
    {
        if (clusters is null || clusters.Count == 0 || _model is null) return;
        var saveTarget = savePath ?? _savePath;

        IsTraining = true;
        OnTrainingStateChanged?.Invoke(true);

        try
        {
            var allData = StackClusters(clusters);

            using (Py.GIL())
            {
                dynamic dataNp = _np!.array(allData.ToArray(), dtype: _np.float32);
                Debug.WriteLine($"[{Name}] Train — shape ({dataNp.shape[0]}, {dataNp.shape[1]})");

                _model!.train_incremental(
                    arr:              dataNp,
                    epochs:           epochs,
                    use_distillation: useDistillation,
                    replay_ratio:     replayRatio,
                    joint_finetune:   jointFinetune,
                    joint_epochs:     jointEpochs,
                    save_to:          saveTarget
                );
            }

            Debug.WriteLine($"[{Name}] Training complete.");
        }
        finally
        {
            IsTraining = false;
            IsTrained  = true;
            OnTrainingStateChanged?.Invoke(false);
        }
    }

    public void FineTune(
        List<(string Label, Matrix Targets, Matrix Data)> clusters,
        int    epochs           = 30,
        int    hop              = 100,
        int    batchSize        = 64,
        double lr               = 3e-4,
        int    oversampleFactor = 3)
    {
        if (clusters is null || clusters.Count == 0 || _model is null) return;

        IsTraining = true;
        OnTrainingStateChanged?.Invoke(true);

        try
        {
            var allData = StackClusters(clusters);

            var labels = allData
                .Column(allData.ColumnCount - 1)
                .Distinct()
                .Select(l => (int)l)
                .OrderBy(l => l)
                .ToList();

            using (Py.GIL())
            {
                dynamic dataNp    = _np!.array(allData.ToArray(), dtype: _np.float32);
                dynamic targetCls = _np.array(labels.ToArray()).tolist();

                _model!.retrain_specific_classes(
                    dataNp,
                    target_classes:    targetCls,
                    epochs:            epochs,
                    hop:               hop,
                    batch_size:        batchSize,
                    lr:                lr,
                    oversample_factor: oversampleFactor
                );
            }

            Debug.WriteLine($"[{Name}] Fine-tune complete.");
        }
        finally
        {
            IsTraining = false;
            OnTrainingStateChanged?.Invoke(false);
        }
    }

    #endregion

    #region Helpers

    private void UpdateDescription() =>
        ModelDescription = $"{ModulePath} | classes={NumClasses} seed={RandomSeed}";

    partial void OnModulePathChanged(string value) => UpdateDescription();
    partial void OnNumClassesChanged(int value)    => UpdateDescription();
    partial void OnRandomSeedChanged(int value)    => UpdateDescription();

    /// <summary>
    /// Appends the class label (taken from each target row's first element) as
    /// the last column of each segment matrix. Because the buffer now stores
    /// one target row per data row, every output row gets the target that was
    /// active when that exact data row arrived — irrelevant for classic
    /// click-driven classification (every target row is identical) but
    /// future-proof against streaming-trigger sources that update mid-segment.
    /// </summary>
    private static Matrix StackClusters(List<(string Label, Matrix Targets, Matrix Data)> clusters)
    {
        var result = Matrix.Build.Dense(0, clusters[0].Data.ColumnCount + 1);
        foreach (var (_, targets, data) in clusters)
        {
            // Build a per-row label column: index target rows 1:1 with data
            // rows. Falls back to row 0 if targets is shorter than data
            // (defensive — shouldn't happen with current buffer semantics).
            var labelCol = Matrix.Build.Dense(data.RowCount, 1);
            for (int r = 0; r < data.RowCount; r++)
            {
                int t = r < targets.RowCount ? r : 0;
                labelCol[r, 0] = targets.ColumnCount > 0 ? targets[t, 0] : 0.0;
            }
            result = result.Stack(data.Append(labelCol));
        }
        return result;
    }

    public override void Dispose()
    {
        using (Py.GIL())
        {
            _model?.Dispose();
            _np?.Dispose();
        }

        _model = null;
        _np    = null;

        Viz.Dispose();
        base.Dispose();
    }

    #endregion
}