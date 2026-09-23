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
/// <para>Block entry for a larger pipeline. Params: Python module, channel count, window length, sample rate, random seed, model path, legacy state-dict path, output count. my_regressor is a placeholder module. Window must provide matching 32-channel, 200-sample data; training targets have 8 values. TrainingBuffer must be a TriggerBuffer.</para>
/// <code language="json">
/// {
///   "PyPredictorRegression": {
///     "Type": "pypredictorregression",
///     "Inputs": ["Window", "TrainingBuffer"],
///     "Params": ["my_regressor", 32, 200, 2000, 42, "", "", 8]
///   }
/// }
/// </code>
/// </example>
public sealed partial class PyPredictorRegression : BaseBlock
{
    /// <summary>Needs exactly two inputs (e.g. a data stream + a Trigger/TriggerBuffer).</summary>
    public override int MinInputs => 2;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 2;

    #region Fields

    private dynamic? _np;
    private dynamic? _model;
    private readonly string _modelPath = string.Empty;
    private DateTime _lastVizUpdate    = DateTime.MinValue;
    private const int VizUpdateIntervalMs = 16; // ~60 fps

    #endregion

    #region Observable Properties

    [ObservableProperty] private string _modulePath       = string.Empty;
    [ObservableProperty] private int    _inChannels       = 32;
    [ObservableProperty] private int    _winLen           = 200;
    [ObservableProperty] private int    _fs               = 2000;
    [ObservableProperty] private int    _randomSeed       = 42;
    [ObservableProperty] private int    _numOutputs       = 8;
    [ObservableProperty] private bool   _skipPreprocess;
    [ObservableProperty] private bool   _isTraining;
    [ObservableProperty] private bool   _isTrained;
    [ObservableProperty] private long   _predictionCount;
    [ObservableProperty] private string _modelDescription = string.Empty;

    // ── Smoothing configuration (bindable from UI) ──────────────────
    [ObservableProperty] private double _smoothTau       = 0.080;
    [ObservableProperty] private double _smoothDeadzone  = 0.08;
    [ObservableProperty] private bool   _smoothEnabled   = true;
    [ObservableProperty] private bool   _smoothAdaptive  = true;

    #endregion

    #region Public Surface

    public BlockVisualization Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    public event Action<Vector>? OnPrediction;
    public event Action<bool>?   OnTrainingStateChanged;
    public event Action?         OnBufferSegmentAdded;

    public TriggerBuffer? Buffer { get; set; }
    public string SavePath => _modelPath;

    #endregion

    #region Constructor & Factory

    public PyPredictorRegression(
        string name,
        double desiredRate,
        string modulePath,
        int    inChannels    = 32,
        int    winLen        = 200,
        int    fs            = 2000,
        int    randomSeed    = 42,
        int    numOutputs    = 8,
        bool   skipPreprocess = false,
        string modelPath     = "")
        : base(name, desiredRate)
    {
        _modulePath = modulePath;
        _inChannels = inChannels;
        _winLen     = winLen;
        _fs         = fs;
        _randomSeed = randomSeed;
        _numOutputs = numOutputs;
        _skipPreprocess = skipPreprocess;
        _modelPath  = modelPath;

        UpdateDescription();

        PythonNetManager.Initialize(PythonNetManager.ResolveConfigPath("config.json"));

        using (Py.GIL())
        {
            Py.Import("torch");
            _np = Py.Import("numpy");
            dynamic godFile = Py.Import(ModulePath);
            var stateDictArg = (!string.IsNullOrEmpty(_modelPath) && System.IO.File.Exists(_modelPath))
                               ? (object)_modelPath
                               : null;
            Debug.WriteLine($"[{Name}] state_dict_path = {(stateDictArg is null ? "None (untrained)" : _modelPath)}");
            _model = godFile.Model(
                state_dict_path: stateDictArg,
                in_ch:           InChannels,
                win_len:         WinLen,
                fs:              Fs,
                random_seed:     RandomSeed,
                num_outputs:     NumOutputs,
                skip_preprocess: SkipPreprocess);

            // Sync smoothing config with Python model
            PushSmoothingConfig();
        }

        using (Py.GIL())
        {
            Console.WriteLine($"[{Name}] Device: {_model!.device}");
            dynamic torch = Py.Import("torch");
            Console.WriteLine($"[{Name}] CUDA available: {torch.cuda.is_available()}");
            if ((bool)torch.cuda.is_available())
                Console.WriteLine($"[{Name}] GPU: {torch.cuda.get_device_name(0)}");
        }
        Console.WriteLine($"[{Name}] Regression model loaded ({NumOutputs} outputs" +
                          $"{(SkipPreprocess ? ", pre-filtered" : "")}): {ModelDescription}");
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_predictions.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_predictions";

    public static PyPredictorRegression ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        if (m.Params is null || m.Params.Count < 1)
            throw new ArgumentException(
                $"[PyPredictorRegression] '{m.Name}' requires at least Params[0]=modulePath.");

        var modulePath   = GetString(m.Params[0], string.Empty);
        var inChannels   = m.Params.Count > 1 ? GetInt(m.Params[1], 32)              : 32;
        var winLen       = m.Params.Count > 2 ? GetInt(m.Params[2], 200)             : 200;
        var fs           = m.Params.Count > 3 ? GetInt(m.Params[3], 2000)            : 2000;
        var randomSeed   = m.Params.Count > 4 ? GetInt(m.Params[4], 42)              : 42;
        var modelPath    = m.Params.Count > 5 ? GetString(m.Params[5], string.Empty) : string.Empty;
        var stateDictPath= m.Params.Count > 6 ? GetString(m.Params[6], string.Empty) : string.Empty;
        var numOutputs   = m.Params.Count > 7 ? GetInt(m.Params[7], 8)              : 8;
        var rate         = m.DesiredRate ?? 200;

        // Check for "preprocess:skip" keyword in Params (same convention as "algorithm:X")
        var skipPreprocess = m.Params.Any(p =>
            (p?.ToString() ?? "").Equals("preprocess:skip", StringComparison.OrdinalIgnoreCase));

        var block = ActivatorUtilities.CreateInstance<PyPredictorRegression>(
            sp, m.Name, rate, modulePath, inChannels, winLen, fs, randomSeed,
            numOutputs, skipPreprocess, modelPath);

        return block;
    }

    #endregion

    #region JSON Export

    protected override string JsonTypeName => "PyPredictorRegression";

    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object> { ModulePath, InChannels, WinLen, Fs, RandomSeed, _modelPath, "", NumOutputs };

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
            dynamic outputNp   = prediction.astype(_np.float64);
            double[] values    = outputNp.reshape(-1).As<double[]>();
            result             = Vector.Build.DenseOfArray(values);
        }

        PredictionCount++;
        Publish(result);
        OnPrediction?.Invoke(result);

        var now = DateTime.UtcNow;
        if ((now - _lastVizUpdate).TotalMilliseconds >= VizUpdateIntervalMs)
        {
            _lastVizUpdate = now;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Viz.Feed(result),
                Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    #endregion

    #region Model Reset (callable from UI / ViewModel)

    /// <summary>
    /// Full reset: reinitialises the model with random weights and clears
    /// all replay buffers, training history, and smoother state.
    /// Call this to start training from scratch.
    /// </summary>
    public void ResetModel()
    {
        if (_model is null) return;
        using (Py.GIL())
        {
            _model.reset();
        }
        IsTrained       = false;
        PredictionCount = 0;
        Console.WriteLine($"[{Name}] Model reset to untrained state.");
    }

    /// <summary>
    /// Reloads the model weights from the checkpoint file that was passed at
    /// construction (state_dict_path). Falls back to a full random reset if
    /// no checkpoint was provided. Also clears replay buffers and smoother.
    /// </summary>
    public void ResetToCheckpoint()
    {
        if (_model is null) return;
        using (Py.GIL())
        {
            _model.reset_to_checkpoint();
        }
        IsTrained       = !string.IsNullOrEmpty(_modelPath);
        PredictionCount = 0;
        Console.WriteLine($"[{Name}] Model reset to checkpoint.");
    }

    /// <summary>
    /// Resets only the output smoother so the next prediction starts fresh.
    /// Useful after a pause or context switch.
    /// </summary>
    public void ResetSmoother()
    {
        if (_model is null) return;
        using (Py.GIL())
        {
            _model.reset_smoother();
        }
    }

    #endregion

    #region Smoothing Configuration

    /// <summary>
    /// Push current C# smoothing properties to the Python model.
    /// Called automatically when any smoothing property changes.
    /// </summary>
    private void PushSmoothingConfig()
    {
        if (_model is null) return;
        try
        {
            _model.configure_smoothing(
                tau:          SmoothTau,
                deadzone:     SmoothDeadzone,
                enabled:      SmoothEnabled,
                adaptive:     SmoothAdaptive,
                pred_rate_hz: DesiredRate);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Smoothing config error: {ex.Message}");
        }
    }

    // Auto-push when any smoothing property changes from UI binding
    partial void OnSmoothTauChanged(double value)
    {
        using (Py.GIL()) PushSmoothingConfig();
    }

    partial void OnSmoothDeadzoneChanged(double value)
    {
        using (Py.GIL()) PushSmoothingConfig();
    }

    partial void OnSmoothEnabledChanged(bool value)
    {
        using (Py.GIL()) PushSmoothingConfig();
    }

    partial void OnSmoothAdaptiveChanged(bool value)
    {
        using (Py.GIL()) PushSmoothingConfig();
    }

    #endregion

    #region Training

    public void TrainIncremental(
        List<(string Label, Matrix Targets, Matrix Data)> clusters,
        int     epochs              = 100,
        int     hop                 = 100,
        int     batchSize           = 32,
        double  lr                  = 1e-3,
        bool    useDistillation     = false,
        double  replayRatio         = 0.0,
        bool    jointFinetune       = false,
        int     jointEpochs         = 30,
        string? savePath            = null)
    {
        if (clusters is null || clusters.Count == 0 || _model is null) return;
        var saveTarget = savePath ?? _modelPath;

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
                    arr:                  dataNp,
                    epochs:               epochs,
                    hop:                  hop,
                    batch_size:           batchSize,
                    lr:                   lr,
                    use_distillation:     useDistillation,
                    replay_ratio:         replayRatio,
                    joint_finetune:       jointFinetune,
                    joint_epochs:         jointEpochs,
                    save_to:              saveTarget,
                    use_validation_split: false,
                    enable_early_stopping: false,
                    weight_decay:         1e-5
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
        int     epochs          = 50,
        int     hop             = 100,
        int     batchSize       = 32,
        double  lr              = 5e-3,
        string? savePath        = null)
    {
        if (clusters is null || clusters.Count == 0 || _model is null) return;
        var saveTarget = savePath ?? _modelPath;

        IsTraining = true;
        OnTrainingStateChanged?.Invoke(true);

        try
        {
            var allData = StackClusters(clusters);

            using (Py.GIL())
            {
                dynamic dataNp = _np!.array(allData.ToArray(), dtype: _np.float32);

                _model!.train_incremental(
                    arr:                  dataNp,
                    epochs:               epochs,
                    hop:                  hop,
                    batch_size:           batchSize,
                    lr:                   lr,
                    use_distillation:     true,
                    distill_weight:       0.2,
                    replay_ratio:         0.9,
                    replay_samples:       500,
                    joint_finetune:       true,
                    joint_epochs:         30,
                    save_to:              saveTarget,
                    use_validation_split: false,
                    enable_early_stopping: false,
                    weight_decay:         1e-5
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
        ModelDescription = $"{ModulePath} | in_ch={InChannels} win={WinLen} fs={Fs} out={NumOutputs}" +
                           $"{(SkipPreprocess ? " [pre-filtered]" : "")} seed={RandomSeed}";

    partial void OnModulePathChanged(string value) => UpdateDescription();
    partial void OnInChannelsChanged(int value)    => UpdateDescription();
    partial void OnWinLenChanged(int value)        => UpdateDescription();
    partial void OnFsChanged(int value)            => UpdateDescription();
    partial void OnRandomSeedChanged(int value)    => UpdateDescription();
    partial void OnNumOutputsChanged(int value)    => UpdateDescription();

    /// <summary>
    /// Stacks segment matrices and appends the per-row target vector as trailing
    /// columns. With the buffer's new (string Label, Matrix Targets, Matrix Data)
    /// shape, each data row has its own target row (1:1 indexing when data is
    /// vector-per-tick; in image cases the caller is responsible for handling
    /// the row-count mismatch upstream). For classification-style senders the
    /// target rows are all identical; for streaming triggers they vary across
    /// time, which is what enables true regression on continuous activations.
    /// </summary>
    private static Matrix StackClusters(List<(string Label, Matrix Targets, Matrix Data)> clusters)
    {
        int targetDim = clusters[0].Targets.ColumnCount;
        int dataCols  = clusters[0].Data.ColumnCount;
        var result    = Matrix.Build.Dense(0, dataCols + targetDim);

        foreach (var (_, targets, data) in clusters)
        {
            var labelBlock = Matrix.Build.Dense(data.RowCount, targetDim);
            for (int row = 0; row < data.RowCount; row++)
            {
                // Map data row → target row 1:1; fall back to row 0 if
                // targets is shorter than data (defensive — shouldn't happen
                // with vector-per-tick sources).
                int t = row < targets.RowCount ? row : 0;
                for (int col = 0; col < targetDim; col++)
                {
                    labelBlock[row, col] = col < targets.ColumnCount ? targets[t, col] : 0.0;
                }
            }
            result = result.Stack(data.Append(labelBlock));
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