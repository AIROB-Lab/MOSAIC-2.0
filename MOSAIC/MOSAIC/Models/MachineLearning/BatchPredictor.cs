using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Components.MachineLearning.Factory;
using MOSAIC.Components.MachineLearning.Interfaces;
using MOSAIC.Models.FlowControl;
using MOSAIC.Visualization;
using static MOSAIC.Components.Basics.JsonModel;

namespace MOSAIC.Models.MachineLearning;

/// <summary>
/// Batch regression block that consumes data collected in a <see cref="TriggerBuffer"/>.
/// Training is <b>not</b> automatic — the UI calls <see cref="TrainFromBuffer"/> explicitly,
/// at which point the model is fit on every segment currently held in
/// <see cref="TriggerBuffer.dB"/>. Predictions on incoming features run continuously while
/// <see cref="IsTraining"/> is <see langword="false"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the workflow of <see cref="PyPredictorRegression"/>: the Buffer is stored as a
/// reference when it publishes (sender-type sniffing in <see cref="OnReceive"/>), an
/// <see cref="OnBufferSegmentAdded"/> event tells the ViewModel that a new segment is
/// available, and training is gated behind a deliberate UI action. Reuses the regressor
/// primitives in <see cref="MOSAIC.Components.MachineLearning"/> directly — sibling to
/// <see cref="IncrementalPredictor"/>, not a subclass.
/// </para>
/// <para>
/// <b>Targets shape:</b> the buffer stores one target <i>row</i> per data row in a
/// <see cref="Matrix{T}"/>. For the classic single-vector <c>Trigger</c> block all rows are
/// identical; for a streaming trigger (per-tick continuous label source) rows vary across
/// the segment. <see cref="TrainFromBuffer"/> reads <c>targets.Row(r)</c> when training so
/// both behaviours are supported transparently.
/// </para>
/// </remarks>
/// <example>
/// <para>Block entry for a larger pipeline. Params: regression model, input dimension, output dimension, regularization, RFF kernel width, RFF feature count. Features must have 8 values and training targets 2 values. TrainingBuffer must be a TriggerBuffer. Provide both inputs through JSON; the editor currently declares only one input.</para>
/// <code language="json">
/// {
///   "BatchPredictor": {
///     "Type": "batchpredictor",
///     "Inputs": ["Features", "TrainingBuffer"],
///     "Params": ["Ridge", 8, 2, 1.0, 1.0, 300]
///   }
/// }
/// </code>
/// </example>
public sealed partial class BatchPredictor : BaseBlock
{
    #region Private Fields

    /// <summary>The active regression model.</summary>
    private IRegressor _model;

    /// <summary>Most recent prediction vector.</summary>
    private Vector<double>? _lastPrediction;

    /// <summary>Directory for model auto-save (from <see cref="JsonModel.Path"/>).</summary>
    private readonly string? _savePath;

    /// <summary>Timestamp of last viz feed for stall detection.</summary>
    private long _lastVizFeedTicks;

    #endregion

    #region Public Properties

    /// <summary>Expected length of incoming feature vectors.</summary>
    public int InputDim { get; }

    /// <summary>Number of prediction output dimensions.</summary>
    public int OutputDim { get; }

    /// <summary>Active regression algorithm type.</summary>
    public RegressorType ModelType { get; private set; }

    /// <summary>Hyperparameters for the active regressor.</summary>
    public RegressorConfig Config { get; private set; }

    /// <summary>
    /// Visualization bundle (Scope/Spider/Heatmap). Auto-initialized — bind a
    /// <c>VisualizationPanel.Source</c> directly to <c>BatchPredictor.Viz</c> from XAML.
    /// </summary>
    public BlockVisualization Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>Most recent prediction vector.</summary>
    public Vector<double>? LastPrediction => _lastPrediction;

    /// <summary>
    /// Reference to the upstream <see cref="TriggerBuffer"/>. Set automatically the first time
    /// the buffer publishes (<see cref="OnReceive"/> identifies it by sender type).
    /// </summary>
    public TriggerBuffer? Buffer { get; set; }

    /// <summary>Save directory for model checkpoints (from <see cref="JsonModel.Path"/>).</summary>
    public string? SavePath => _savePath;

    #endregion

    #region Observable Properties

    /// <summary>Raw confidence value from the regressor for the latest prediction.</summary>
    [ObservableProperty]
    private double _confidence;

    /// <summary>Total training samples processed across all <see cref="TrainFromBuffer"/> calls.</summary>
    [ObservableProperty]
    private int _sampleCount;

    /// <summary>Total Buffer segments trained on across all <see cref="TrainFromBuffer"/> calls.</summary>
    [ObservableProperty]
    private int _segmentsProcessed;

    /// <summary>Human-readable label of the most recently trained segment.</summary>
    [ObservableProperty]
    private string _currentTargetName = "None";

    /// <summary>
    /// When <see langword="true"/>, <see cref="TrainFromBuffer"/> resets the regressor before
    /// fitting. Default <see langword="false"/> — additive across multiple Train clicks.
    /// </summary>
    [ObservableProperty]
    private bool _resetBeforeTraining;

    /// <summary>True while <see cref="TrainFromBuffer"/> is running. Predictions are skipped during this window.</summary>
    [ObservableProperty]
    private bool _isTraining;

    /// <summary>True after the first successful <see cref="TrainFromBuffer"/> call.</summary>
    [ObservableProperty]
    private bool _isTrained;

    #endregion

    #region Events

    /// <summary>Fired after every prediction. Args: (prediction, target, confidence, sampleCount).</summary>
    /// <remarks>The <c>target</c> argument is always <see langword="null"/> for BatchPredictor.</remarks>
    public event Action<Vector<double>, Vector<double>?, double, int>? OnPrediction;

    /// <summary>Fired when <see cref="IsTraining"/> changes. Args: new IsTraining value.</summary>
    public event Action<bool>? OnTrainingStateChanged;

    /// <summary>Fired when the upstream <see cref="TriggerBuffer"/> publishes (i.e. a new segment landed).</summary>
    public event Action? OnBufferSegmentAdded;

    #endregion

    #region Constructors

    public BatchPredictor(
        string name,
        double desiredRate,
        int inputDim,
        int outputDim,
        RegressorType modelType,
        RegressorConfig config,
        string? savePath) : base(name, desiredRate)
    {
        InputDim   = inputDim;
        OutputDim  = outputDim;
        ModelType  = modelType;
        Config     = config;
        _savePath  = savePath;

        _model = RegressorFactory.Create(ModelType, InputDim, OutputDim, Config);

        Debug.WriteLine($"[{Name}] Initialized: {ModelType}, InputDim={InputDim}, OutputDim={OutputDim}");
    }

    public BatchPredictor(
        string name, double desiredRate,
        int inputDim, int outputDim,
        RegressorType modelType, RegressorConfig config)
        : this(name, desiredRate, inputDim, outputDim, modelType, config, null) { }

    public BatchPredictor(
        string name, double desiredRate,
        int inputDim, int outputDim)
        : this(name, desiredRate, inputDim, outputDim, RegressorType.Ridge, new RegressorConfig(), null) { }

    #endregion

    #region Factory

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_predictions.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_predictions";

    public static BatchPredictor ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "BatchPredictor";
        var rate = m.DesiredRate ?? 0;

        if (m.Params is null || m.Params.Count < 3)
            throw new ArgumentException(
                $"BatchPredictor '{name}' requires params: [modelType, inputDim, outputDim, lambda?, sigma?, featureDim?]");

        var modelTypeStr = GetString(m.Params[0], "Ridge");
        if (!Enum.TryParse<RegressorType>(modelTypeStr, ignoreCase: true, out var modelType))
            throw new ArgumentException(
                $"Unknown model type '{modelTypeStr}'. Available: {string.Join(", ", Enum.GetNames<RegressorType>())}");

        var inputDim  = GetInt(m.Params[1]);
        var outputDim = GetInt(m.Params[2]);

        var config = new RegressorConfig
        {
            Lambda     = m.Params.Count > 3 ? GetDouble(m.Params[3], 1.0) : 1.0,
            Sigma      = m.Params.Count > 4 ? GetDouble(m.Params[4], 1.0) : 1.0,
            FeatureDim = m.Params.Count > 5 ? GetInt(m.Params[5], 300)    : 300
        };

        var block = new BatchPredictor(name, rate, inputDim, outputDim, modelType, config, m.Path);
        return block;
    }

    #endregion

    #region JSON Export

    protected override string JsonTypeName => "BatchPredictor";

    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object> { ModelType.ToString(), InputDim, OutputDim, Config.Lambda, Config.Sigma, Config.FeatureDim };

    protected override string? GetJsonPath() => _savePath;

    #endregion

    #region OnReceive

    protected override void OnReceive(object sender, object data)
    {
        if (sender is TriggerBuffer tb)
        {
            Buffer = tb;
            try { OnBufferSegmentAdded?.Invoke(); }
            catch (Exception ex) { Debug.WriteLine($"[{Name}] OnBufferSegmentAdded handler error: {ex.Message}"); }
            return;
        }

        if (IsTraining || _model is null) return;

        try
        {
            switch (data)
            {
                case Vector<double> x when x.Count == InputDim:
                    HandleFeatureInput(x);
                    break;

                case Matrix<double> mat when mat.ColumnCount == InputDim:
                    for (int r = 0; r < mat.RowCount; r++)
                    {
                        if (r < mat.RowCount - 1)
                            HandleFeatureInputSilent(mat.Row(r));
                        else
                            HandleFeatureInput(mat.Row(r));
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] OnReceive error: {ex.Message}");
        }
    }

    #endregion

    #region Buffer-driven Training (UI-initiated)

    /// <summary>
    /// Trains the regressor on every segment currently held in the upstream
    /// <see cref="TriggerBuffer.dB"/>. UI-driven — nothing in the data path triggers this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With the buffer's new per-row target shape, each data row <c>r</c> gets paired with
    /// <c>targets.Row(r)</c> — so the regressor sees correctly aligned (feature, target)
    /// pairs regardless of whether the trigger was a single-shot <c>Trigger</c> (identical
    /// rows) or a continuous <c>StreamTrigger</c> (varying rows).
    /// </para>
    /// <para>
    /// If <see cref="ResetBeforeTraining"/> is <see langword="true"/>, the model is reset
    /// (and counters cleared) before fitting begins. Otherwise this call <i>adds</i> to
    /// whatever the model has already learned. Predictions are paused for the duration;
    /// dimension-mismatched segments are logged and skipped without throwing.
    /// </para>
    /// </remarks>
    public void TrainFromBuffer()
    {
        if (Buffer is null)
        {
            Debug.WriteLine($"[{Name}] TrainFromBuffer aborted — no Buffer reference. " +
                            "Wire a TriggerBuffer as an input and capture at least one segment first.");
            return;
        }

        var segments = Buffer.dB.ToList();
        if (segments.Count == 0)
        {
            Debug.WriteLine($"[{Name}] TrainFromBuffer aborted — Buffer.dB is empty.");
            return;
        }

        IsTraining = true;
        try { OnTrainingStateChanged?.Invoke(true); }
        catch (Exception ex) { Debug.WriteLine($"[{Name}] OnTrainingStateChanged handler error: {ex.Message}"); }

        bool anyTrained = false;

        try
        {
            if (ResetBeforeTraining)
            {
                _model.Reset();
                SampleCount       = 0;
                SegmentsProcessed = 0;
                Debug.WriteLine($"[{Name}] Model reset before training.");
            }

            int totalRows = 0;

            foreach (var (label, targets, data) in segments)
            {
                if (targets.ColumnCount != OutputDim)
                {
                    Debug.WriteLine($"[{Name}] Segment '{label}' target cols {targets.ColumnCount} != OutputDim {OutputDim}, skipped");
                    continue;
                }
                if (data.ColumnCount != InputDim)
                {
                    Debug.WriteLine($"[{Name}] Segment '{label}' data cols {data.ColumnCount} != InputDim {InputDim}, skipped");
                    continue;
                }
                if (data.RowCount == 0) continue;

                // Per-row target lookup — row r of data pairs with row r of targets,
                // falling back to row 0 if targets is shorter (defensive — shouldn't
                // happen but won't crash if a trigger misbehaves).
                for (int r = 0; r < data.RowCount; r++)
                {
                    int t = r < targets.RowCount ? r : 0;
                    _model.Update(data.Row(r), targets.Row(t));
                }

                SampleCount       += data.RowCount;
                SegmentsProcessed += 1;
                totalRows         += data.RowCount;
                CurrentTargetName  = $"{label} ({data.RowCount} samples)";
                anyTrained = true;
            }

            if (anyTrained)
            {
                IsTrained = true;
                Debug.WriteLine(
                    $"[{Name}] TrainFromBuffer complete — {segments.Count} segments, " +
                    $"{totalRows} samples this call, {SampleCount} cumulative");
            }
            else
            {
                Debug.WriteLine($"[{Name}] TrainFromBuffer ran but trained on no segments (all skipped).");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] TrainFromBuffer error: {ex.Message}");
        }
        finally
        {
            IsTraining = false;
            try { OnTrainingStateChanged?.Invoke(false); }
            catch (Exception ex) { Debug.WriteLine($"[{Name}] OnTrainingStateChanged handler error: {ex.Message}"); }
        }
    }

    #endregion

    #region Prediction

    private void HandleFeatureInput(Vector<double> x)
    {
        try
        {
            var t0 = Stopwatch.GetTimestamp();

            Confidence       = _model.Confidence(x);
            var prediction   = _model.Predict(x);
            _lastPrediction  = prediction;

            try { OnPrediction?.Invoke(prediction, null, Confidence, SampleCount); }
            catch (Exception ex) { Debug.WriteLine($"[{Name}] OnPrediction handler error: {ex.Message}"); }

            var nowTicks         = Stopwatch.GetTimestamp();
            var processingMs     = (nowTicks - t0) * 1000.0 / Stopwatch.Frequency;
            var sinceLastFeedMs  = _lastVizFeedTicks == 0
                ? 0.0
                : (nowTicks - _lastVizFeedTicks) * 1000.0 / Stopwatch.Frequency;

            if (processingMs < 500 && sinceLastFeedMs < 1500)
                Viz.Feed(prediction);

            _lastVizFeedTicks = nowTicks;
            Publish(prediction);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Prediction error: {ex.Message}");
        }
    }

    private void HandleFeatureInputSilent(Vector<double> x)
    {
        try
        {
            _lastPrediction = _model.Predict(x);
            Confidence      = _model.Confidence(x);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Silent prediction error: {ex.Message}");
        }
    }

    #endregion

    #region Public API (model lifecycle, save/load)

    public void ChangeModel(RegressorType type, RegressorConfig? config = null)
    {
        ModelType         = type;
        Config            = config ?? Config;
        _model            = RegressorFactory.Create(ModelType, InputDim, OutputDim, Config);
        SampleCount       = 0;
        SegmentsProcessed = 0;
        IsTrained         = false;
        Confidence        = 0;
    }

    public void ResetModel()
    {
        _model.Reset();
        SampleCount       = 0;
        SegmentsProcessed = 0;
        IsTrained         = false;
        Confidence        = 0;
        Debug.WriteLine($"[{Name}] Model reset");
    }

    public void SaveModel(string? path = null)
    {
        path ??= GetDefaultSavePath();
        if (path is null) return;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (_model is IStateful<RidgeState> stateful)
        {
            ModelStateSerializer.Save(stateful.GetState(), path);
            Debug.WriteLine($"[{Name}] Model saved to {path} ({SampleCount} samples)");
        }
    }

    public void LoadModel(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Model file not found: {path}");

        if (_model is IStateful<RidgeState> stateful)
        {
            stateful.SetState(ModelStateSerializer.Load(path));
            IsTrained = true;
            Debug.WriteLine($"[{Name}] Model loaded from {path}");
        }
    }

    public void AutoSave()
    {
        if (SampleCount == 0) return;
        var path = GetDefaultSavePath();
        if (path is not null) SaveModel(path);
    }

    private string? GetDefaultSavePath()
    {
        if (_savePath is null) return null;
        Directory.CreateDirectory(_savePath);
        return Path.Combine(_savePath, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Name}_{SampleCount}samples.json");
    }

    #endregion

    #region Dispose

    public override void Dispose()
    {
        try { Viz?.Dispose(); } catch { /* no-op */ }
        if (SampleCount > 0) AutoSave();
        base.Dispose();
    }

    #endregion
}