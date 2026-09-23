using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Components.MachineLearning.Factory;
using MOSAIC.Components.MachineLearning.Interfaces;
using MOSAIC.Visualization;
using static MOSAIC.Components.Basics.JsonModel;

namespace MOSAIC.Models.MachineLearning;

/// <summary>
/// Incremental regression block that learns to map input feature vectors to
/// multi-dimensional output predictions in real time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> The <see cref="IncrementalPredictor"/> sits between a feature-extraction
/// pipeline (e.g. EMG → Filter → Features) and a control algorithm. It learns an online mapping
/// from input features to output activations using incremental ridge regression (optionally with
/// Random Fourier Features for non-linear kernels). Training is driven by a
/// <see cref="MOSAIC.Models.FlowControl.Trigger"/> block that supplies labeled target vectors.
/// </para>
///
/// <para><b>Inputs:</b> Expects exactly two upstream connections:</para>
/// <list type="bullet">
///   <item><description>
///     <b>Feature source</b> — any block publishing <c>Vector&lt;double&gt;</c> of length
///     <see cref="InputDim"/>, or a <c>Matrix&lt;double&gt;</c> of shape
///     <c>[timesteps × InputDim]</c> where each row is processed as an individual feature vector.
///   </description></item>
///   <item><description>
///     <b>Trigger</b> — publishes a target <c>Vector&lt;double&gt;</c> of length
///     <see cref="OutputDim"/> to start training, or <see langword="null"/> to stop.
///   </description></item>
/// </list>
///
/// <para><b>Output:</b> After every feature input, publishes a prediction <c>Vector&lt;double&gt;</c>
/// of length <see cref="OutputDim"/>. For matrix input, only the last row's prediction is published
/// (all rows are trained on if training is active).</para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "Predictor": {
///     "Type": "IncrementalPredictor",
///     "DesiredRate": 200,
///     "Inputs": [ "LowpassFilter", "Trigger" ],
///     "Params": [ "RidgeRFF", 8, 12, 0.5, 1.0, 300 ],
///     "Path": "C:/recordings"
///   }
/// }
/// </code>
/// </example>
public sealed partial class IncrementalPredictor : BaseBlock
{
    /// <summary>Needs exactly two inputs (e.g. a data stream + a Trigger/TriggerBuffer).</summary>
    public override int MinInputs => 2;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 2;

    /// <summary>The active regression model.</summary>
    private IRegressor _model;

    /// <summary>Current training target vector, or <see langword="null"/> when not training.</summary>
    private Vector<double>? _currentTarget;

    /// <summary>Most recent prediction vector.</summary>
    private Vector<double>? _lastPrediction;

    /// <summary>Directory for model auto-save.</summary>
    private readonly string? _savePath;

    /// <summary>Timestamp of last viz feed for stall detection.</summary>
    private long _lastVizFeedTicks;

    /// <summary>Expected length of incoming feature vectors.</summary>
    public int InputDim { get; set; }

    /// <summary>Number of prediction output dimensions.</summary>
    public int OutputDim { get; set; }

    /// <summary>Active regression algorithm type.</summary>
    public RegressorType ModelType { get; private set; }

    /// <summary>Hyperparameters for the active regressor.</summary>
    public RegressorConfig Config { get; private set; }

    /// <summary>Visualization bundle set by the ViewModel.</summary>
    private BlockVisualization? _viz;

    /// <summary>Live plot for this block. Assigned by the ViewModel, which owns the scope.</summary>
    /// <remarks>
    /// Setting this re-pushes the publish rate: the rate is normally pushed when it changes,
    /// which for these blocks happens during construction — before the ViewModel has handed
    /// over the scope — so without this the scope would never learn its time base.
    /// </remarks>
    public BlockVisualization? Viz
    {
        get => _viz;
        set { _viz = value; RefreshVisualizationRate(); }
    }

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>Whether the block is currently training.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrainingStatusText))]
    private bool _isTraining;

    /// <summary>Raw confidence value from the regressor.</summary>
    [ObservableProperty]
    private double _confidence;

    /// <summary>Total training samples processed.</summary>
    [ObservableProperty]
    private int _sampleCount;

    /// <summary>Name of the current training target.</summary>
    [ObservableProperty]
    private string _currentTargetName = "None";

    /// <summary>Human-readable training status for UI.</summary>
    public string TrainingStatusText => IsTraining ? $"Training: {CurrentTargetName}" : "Predicting";

    /// <summary>Per-output-column labels from the Trigger's action dictionary.</summary>
    public List<string>? OutputLabels { get; private set; }

    /// <summary>Fired after every prediction.</summary>
    public event Action<Vector<double>, Vector<double>?, double, int>? OnPrediction;

    /// <summary>Fired when training starts/stops.</summary>
    public event Action<bool, string?>? OnTrainingStateChanged;

    /// <summary>Fired once when output labels are derived.</summary>
    public event Action<List<string>>? OnLabelsUpdated;

    /// <summary>Most recent prediction vector.</summary>
    public Vector<double>? LastPrediction => _lastPrediction;

    #region Constructors

    public IncrementalPredictor(
        string name,
        double desiredRate,
        int inputDim,
        int outputDim,
        RegressorType modelType,
        RegressorConfig config,
        string? savePath) : base(name, desiredRate)
    {
        InputDim = inputDim;
        OutputDim = outputDim;
        ModelType = modelType;
        Config = config;
        _savePath = savePath;

        _model = RegressorFactory.Create(ModelType, InputDim, OutputDim, Config);

        Debug.WriteLine($"[{Name}] Initialized: {ModelType}, InputDim={InputDim}, OutputDim={OutputDim}");
    }

    public IncrementalPredictor(
        string name, double desiredRate,
        int inputDim, int outputDim,
        RegressorType modelType, RegressorConfig config)
        : this(name, desiredRate, inputDim, outputDim, modelType, config, null) { }

    public IncrementalPredictor(
        string name, double desiredRate,
        int inputDim, int outputDim)
        : this(name, desiredRate, inputDim, outputDim, RegressorType.Ridge, new RegressorConfig(), null) { }

    #endregion

    #region Factory

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_predictions.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_predictions";

    public static IncrementalPredictor ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "IncrementalPredictor";
        var rate = m.DesiredRate ?? 0;

        if (m.Params is null || m.Params.Count < 3)
            throw new ArgumentException(
                $"IncrementalPredictor '{name}' requires params: [modelType, inputDim, outputDim, lambda?, sigma?, featureDim?]");

        var modelTypeStr = GetString(m.Params[0], "Ridge");
        if (!Enum.TryParse<RegressorType>(modelTypeStr, ignoreCase: true, out var modelType))
            throw new ArgumentException(
                $"Unknown model type '{modelTypeStr}'. Available: {string.Join(", ", Enum.GetNames<RegressorType>())}");

        var inputDim = GetInt(m.Params[1]);
        var outputDim = GetInt(m.Params[2]);

        var config = new RegressorConfig
        {
            Lambda     = m.Params.Count > 3 ? GetDouble(m.Params[3], 1.0) : 1.0,
            Sigma      = m.Params.Count > 4 ? GetDouble(m.Params[4], 1.0) : 1.0,
            FeatureDim = m.Params.Count > 5 ? GetInt(m.Params[5], 300)    : 300
        };

        var block = new IncrementalPredictor(name, rate, inputDim, outputDim, modelType, config, m.Path);
        return block;
    }

    #endregion

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "IncrementalPredictor";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object> { ModelType.ToString(), InputDim, OutputDim, Config.Lambda, Config.Sigma, Config.FeatureDim };

    /// <inheritdoc />
    protected override string? GetJsonPath() => _savePath;

    #endregion

    #region OnReceive

    /// <summary>
    /// Dispatches incoming data. Trigger inputs control training state;
    /// feature inputs (Vector or Matrix) drive prediction.
    /// </summary>
    protected override void OnReceive(object sender, object data)
    {
        try
        {
            var senderName = sender?.GetType().Name ?? "";
            var isTrigger = senderName == "Trigger" || senderName.Contains("Trigger");

            if (isTrigger)
            {
                HandleTriggerInput(sender, data);
                return;
            }

            switch (data)
            {
                case Vector<double> x when x.Count == InputDim:
                    HandleFeatureInput(x);
                    break;

                case Matrix<double> mat when mat.ColumnCount == InputDim:
                    // Process each row as an individual feature vector.
                    // All rows are trained on (if training), but only the last
                    // row's prediction is published and visualized.
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

    #region Feature Processing

    /// <summary>
    /// Processes Trigger messages: target vectors start training, null stops it.
    /// </summary>
    private void HandleTriggerInput(object? sender, object? data)
    {
        if (OutputLabels is null)
            TryExtractLabels(sender);

        if (data is Vector<double> target)
        {
            _currentTarget = target;
            IsTraining = true;

            var prop = sender?.GetType().GetProperty("CurrentActionName");
            CurrentTargetName = prop?.GetValue(sender)?.ToString() ?? "Unknown";

            OnTrainingStateChanged?.Invoke(true, CurrentTargetName);
            Debug.WriteLine($"[{Name}] Training STARTED: {CurrentTargetName}");
        }
        else if (data is null)
        {
            var wasTraining = IsTraining;
            _currentTarget = null;
            IsTraining = false;
            CurrentTargetName = "None";

            Debug.WriteLine($"[{Name}] Training STOPPED (was training: {wasTraining})");

            if (wasTraining)
            {
                AutoSave();
                OnTrainingStateChanged?.Invoke(false, null);
            }
        }
    }

    /// <summary>
    /// Processes a feature vector: trains (if active), predicts, publishes, and visualizes.
    /// </summary>
    private void HandleFeatureInput(Vector<double> x)
    {
        try
        {
            var t0 = Stopwatch.GetTimestamp();

            if (IsTraining && _currentTarget is not null)
            {
                _model.Update(x, _currentTarget);
                SampleCount++;
            }

            Confidence = _model.Confidence(x);
            var prediction = _model.Predict(x);
            _lastPrediction = prediction;

            try { OnPrediction?.Invoke(prediction, _currentTarget, Confidence, SampleCount); }
            catch (Exception ex) { Debug.WriteLine($"[{Name}] OnPrediction handler error: {ex.Message}"); }

            var nowTicks = Stopwatch.GetTimestamp();
            var processingMs = (nowTicks - t0) * 1000.0 / Stopwatch.Frequency;
            var sinceLastFeedMs = _lastVizFeedTicks == 0
                ? 0.0
                : (nowTicks - _lastVizFeedTicks) * 1000.0 / Stopwatch.Frequency;

            if (processingMs < 500 && sinceLastFeedMs < 1500)
                Viz?.Feed(prediction);

            _lastVizFeedTicks = nowTicks;
            Publish(prediction);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Prediction error: {ex.Message}");
        }
    }

    /// <summary>
    /// Processes a feature vector for training and prediction only — no Publish, no Viz feed.
    /// Used for intermediate rows of a matrix input so only the last row triggers output.
    /// </summary>
    private void HandleFeatureInputSilent(Vector<double> x)
    {
        try
        {
            if (IsTraining && _currentTarget is not null)
            {
                _model.Update(x, _currentTarget);
                SampleCount++;
            }

            _lastPrediction = _model.Predict(x);
            Confidence = _model.Confidence(x);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Silent prediction error: {ex.Message}");
        }
    }

    #endregion

    #region Label Extraction

    private void TryExtractLabels(object? sender)
    {
        if (sender is null) return;

        try
        {
            var prop = sender.GetType().GetProperty("Actions");
            if (prop?.GetValue(sender) is not IDictionary<string, Vector<double>> actions || actions.Count == 0)
                return;

            var columnLabels = new string[OutputDim];
            var columnCounts = new Dictionary<string, int>();

            for (int col = 0; col < OutputDim; col++)
            {
                string bestAction = $"Out[{col}]";
                double bestValue = 0;

                foreach (var (name, vec) in actions)
                {
                    if (name.Equals("rest", StringComparison.OrdinalIgnoreCase)) continue;
                    if (col >= vec.Count) continue;

                    if (Math.Abs(vec[col]) > bestValue)
                    {
                        bestValue = Math.Abs(vec[col]);
                        bestAction = name;
                    }
                }

                columnLabels[col] = bestAction;
                columnCounts[bestAction] = columnCounts.GetValueOrDefault(bestAction) + 1;
            }

            var runningIdx = new Dictionary<string, int>();
            for (int col = 0; col < OutputDim; col++)
            {
                var action = columnLabels[col];
                if (columnCounts.GetValueOrDefault(action) > 1)
                {
                    var idx = runningIdx.GetValueOrDefault(action);
                    columnLabels[col] = $"{action}[{idx}]";
                    runningIdx[action] = idx + 1;
                }
            }

            OutputLabels = new List<string>(columnLabels);
            Debug.WriteLine($"[{Name}] Labels from Trigger: [{string.Join(", ", OutputLabels)}]");
            OnLabelsUpdated?.Invoke(OutputLabels);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Label extraction failed: {ex.Message}");
        }
    }

    #endregion

    #region Public API

    public void SetTarget(Vector<double>? target, string? targetName = null)
    {
        _currentTarget = target;
        IsTraining = target is not null;
        CurrentTargetName = targetName ?? (target is not null ? "Manual" : "None");

        OnTrainingStateChanged?.Invoke(IsTraining, CurrentTargetName);
        if (target is null) AutoSave();
    }

    public void ChangeModel(RegressorType type, RegressorConfig? config = null)
    {
        ModelType = type;
        Config = config ?? Config;
        _model = RegressorFactory.Create(ModelType, InputDim, OutputDim, Config);
        SampleCount = 0;
        Confidence = 0;
    }

    public void ResetModel()
    {
        _model.Reset();
        SampleCount = 0;
        Confidence = 0;
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