using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Components.MachineLearning.Classifier;
using MOSAIC.Components.MachineLearning.Factory;
using MOSAIC.Components.MachineLearning.Interfaces;
using MOSAIC.MachineLearning.Classification;
using MOSAIC.Models.FlowControl;
using MOSAIC.Visualization;
using static MOSAIC.Components.Basics.JsonModel;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Models.MachineLearning;

/// <summary>
/// Training mode for the <see cref="ClassifierBlock"/>.
/// </summary>
public enum ClassifierTrainingMode
{
    /// <summary>
    /// Batch mode: reads labelled segments from a <see cref="TriggerBuffer"/>,
    /// retrains the full model on every new segment.
    /// </summary>
    Buffer,

    /// <summary>
    /// Incremental mode: the Trigger block drives live training directly.
    /// </summary>
    Incremental
}

/// <summary>
/// Classification block supporting both batch training from a <see cref="TriggerBuffer"/>
/// and incremental online learning from a <see cref="Trigger"/>.
/// </summary>
/// <example>
/// <para>Block entry for a larger pipeline. Params: classifier model, feature dimension, class count, training mode. Features must have one value; TrainingBuffer must be a TriggerBuffer containing labelled segments for two classes. Capture and train before interpreting predictions.</para>
/// <code language="json">
/// {
///   "Classifier": {
///     "Type": "classifier",
///     "Inputs": ["Features", "TrainingBuffer"],
///     "Params": ["KNN", 1, 2, "Buffer"]
///   }
/// }
/// </code>
/// </example>
public sealed partial class ClassifierBlock : BaseBlock
{
    /// <summary>Needs exactly two inputs (e.g. a data stream + a Trigger/TriggerBuffer).</summary>
    public override int MinInputs => 2;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 2;

    #region Fields

    private IClassifier _model;
    private TriggerBuffer? _triggerBuffer;
    private int _lastKnownSegmentCount;
    private int _currentLabel = -1;
    private long _lastVizFeedTicks;

    #endregion

    #region Properties

    public int InputDim { get; }

    [ObservableProperty]
    private int _numClasses;

    public ClassifierType ModelType { get; private set; }
    public ClassifierConfig Config { get; private set; }

    [ObservableProperty]
    private ClassifierTrainingMode _trainingMode;

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

    [ObservableProperty]
    private double _confidence;

    [ObservableProperty]
    private int _sampleCount;

    [ObservableProperty]
    private int _segmentCount;

    [ObservableProperty]
    private int _lastPredictedClass;

    [ObservableProperty]
    private bool _isTrained;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrainingStatusText))]
    private bool _isTraining;

    [ObservableProperty]
    private string _currentTargetName = "None";

    public string TrainingStatusText => TrainingMode switch
    {
        ClassifierTrainingMode.Buffer => IsTrained
            ? $"Trained ({SampleCount} samples)"
            : $"Awaiting data ({SegmentCount} segments)",
        ClassifierTrainingMode.Incremental => IsTraining
            ? $"Training: {CurrentTargetName}"
            : "Predicting",
        _ => "Idle"
    };

    public List<string> ClassLabels { get; private set; } = new();
    public Vector? LastProbabilities { get; private set; }

    #endregion

    #region Events

    public event Action<int, double[], double, int>? OnPrediction;
    public event Action<int, int>? OnRetrained;
    public event Action<List<string>>? OnLabelsUpdated;
    public event Action<bool, string?>? OnTrainingStateChanged;

    #endregion

    #region Constructors

    public ClassifierBlock(
        string name,
        double desiredRate,
        int inputDim,
        int numClasses,
        ClassifierType modelType,
        ClassifierConfig config,
        ClassifierTrainingMode trainingMode = ClassifierTrainingMode.Buffer) : base(name, desiredRate)
    {
        InputDim = inputDim;
        _numClasses = numClasses;
        ModelType = modelType;
        Config = config;
        _trainingMode = trainingMode;

        _model = ClassifierFactory.Create(ModelType, InputDim, NumClasses, Config);

        Debug.WriteLine($"[{Name}] Initialized: {ModelType}, InputDim={InputDim}, " +
                        $"NumClasses={NumClasses}, Mode={TrainingMode}");
    }

    #endregion

    #region Factory

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_classifications.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_classifications";

    public static ClassifierBlock ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "ClassifierBlock";
        var rate = m.DesiredRate ?? 0;

        if (m.Params is null || m.Params.Count < 3)
            throw new ArgumentException(
                $"ClassifierBlock '{name}' requires params: [modelType, inputDim, numClasses, mode?]");

        var modelTypeStr = GetString(m.Params[0], "LDA");
        if (!Enum.TryParse<ClassifierType>(modelTypeStr, ignoreCase: true, out var modelType))
            throw new ArgumentException(
                $"Unknown classifier type '{modelTypeStr}'. Available: {string.Join(", ", Enum.GetNames<ClassifierType>())}");

        var inputDim = GetInt(m.Params[1]);
        var numClasses = GetInt(m.Params[2]);

        var modeStr = m.Params.Count > 3 ? GetString(m.Params[3], "Buffer") : "Buffer";
        if (!Enum.TryParse<ClassifierTrainingMode>(modeStr, ignoreCase: true, out var mode))
            mode = ClassifierTrainingMode.Buffer;

        var config = new ClassifierConfig();

        var block = new ClassifierBlock(name, rate, inputDim, numClasses, modelType, config, mode);
        return block;
    }

    #endregion

    #region JSON Export

    protected override string JsonTypeName => "ClassifierBlock";

    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object> { ModelType.ToString(), InputDim, NumClasses, TrainingMode.ToString() };

    #endregion

    #region OnReceive

    protected override void OnReceive(object sender, object data)
    {
        try
        {
            if (sender is TriggerBuffer tb)
            {
                _triggerBuffer = tb;
                if (TrainingMode == ClassifierTrainingMode.Buffer
                    && data is int segCount && segCount > _lastKnownSegmentCount)
                {
                    _lastKnownSegmentCount = segCount;
                    SegmentCount = segCount;
                    RetrainFromBuffer();
                }
                return;
            }

            var senderName = sender?.GetType().Name ?? "";
            if (senderName == "Trigger" || senderName.Contains("Trigger"))
            {
                if (TrainingMode == ClassifierTrainingMode.Incremental)
                    HandleTriggerInput(sender, data);
                return;
            }

            if (data is Vector x && x.Count == InputDim)
                HandleFeatureInput(x);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] OnReceive error: {ex.Message}");
        }
    }

    #endregion

    #region Trigger Handling (Incremental Mode)

    private void HandleTriggerInput(object? sender, object? data)
    {
        if (data is Vector target)
        {
            // Class index = argmax of target vector. Done inline instead of via
            // MathNet's MaximumIndex() extension, which isn't reliably present
            // on every MathNet version. Equivalent semantics, more portable.
            _currentLabel = ArgMax(target);
            IsTraining = true;

            var prop = sender?.GetType().GetProperty("CurrentActionName");
            CurrentTargetName = prop?.GetValue(sender)?.ToString() ?? $"Class {_currentLabel}";

            OnTrainingStateChanged?.Invoke(true, CurrentTargetName);
            Debug.WriteLine($"[{Name}] Incremental training STARTED: {CurrentTargetName} (label={_currentLabel})");
        }
        else if (data is null)
        {
            var wasTraining = IsTraining;
            _currentLabel = -1;
            IsTraining = false;
            CurrentTargetName = "None";

            if (wasTraining)
                OnTrainingStateChanged?.Invoke(false, null);

            Debug.WriteLine($"[{Name}] Incremental training STOPPED");
        }
    }

    /// <summary>Inline argmax for a Vector — index of the largest element.</summary>
    private static int ArgMax(Vector v)
    {
        if (v.Count == 0) return -1;
        int idx = 0;
        double best = v[0];
        for (int i = 1; i < v.Count; i++)
        {
            if (v[i] > best) { best = v[i]; idx = i; }
        }
        return idx;
    }

    /// <summary>Inline argmax across a Matrix row — index of the largest element in that row.</summary>
    private static int ArgMaxRow(Matrix m, int row)
    {
        if (m.ColumnCount == 0) return -1;
        int idx = 0;
        double best = m[row, 0];
        for (int j = 1; j < m.ColumnCount; j++)
        {
            if (m[row, j] > best) { best = m[row, j]; idx = j; }
        }
        return idx;
    }

    #endregion

    #region Feature Processing

    private void HandleFeatureInput(Vector x)
    {
        try
        {
            if (TrainingMode == ClassifierTrainingMode.Incremental
                && IsTraining && _currentLabel >= 0)
            {
                NumClasses = Math.Max(NumClasses, _currentLabel + 1);

                if (_model.NumClasses < NumClasses)
                {
                    _model = ClassifierFactory.Create(ModelType, InputDim, NumClasses, Config);
                }

                _model.Update(x, _currentLabel);
                SampleCount++;
                IsTrained = true;
            }

            var predictedClass = _model.Predict(x);
            var probabilities = _model.PredictProbabilities(x);
            var confidence = _model.Confidence(x);

            LastPredictedClass = predictedClass;
            LastProbabilities = Vector.Build.DenseOfArray(probabilities);
            Confidence = confidence;

            try { OnPrediction?.Invoke(predictedClass, probabilities, confidence, SampleCount); }
            catch (Exception ex) { Debug.WriteLine($"[{Name}] OnPrediction handler error: {ex.Message}"); }

            var nowTicks = Stopwatch.GetTimestamp();
            var sinceLastFeedMs = _lastVizFeedTicks == 0
                ? 0.0
                : (nowTicks - _lastVizFeedTicks) * 1000.0 / Stopwatch.Frequency;

            if (sinceLastFeedMs < 1500)
                Viz?.Feed(LastProbabilities);

            _lastVizFeedTicks = nowTicks;
            Publish(LastProbabilities);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Prediction error: {ex.Message}");
        }
    }

    #endregion

    #region Batch Training from TriggerBuffer

    /// <summary>
    /// Reads all segments from the TriggerBuffer, flattens them into (feature, label)
    /// pairs, and retrains the active classifier. With the new buffer protocol, target
    /// rows are per-data-row; for classification every row of a segment's Targets matrix
    /// is identical (the trigger held a single class for the whole capture), so we read
    /// row 0 and argmax across columns to recover the integer class index.
    /// </summary>
    private void RetrainFromBuffer()
    {
        if (_triggerBuffer == null || _triggerBuffer.dB.Count == 0)
        {
            Debug.WriteLine($"[{Name}] No training data in TriggerBuffer.");
            return;
        }

        var features = new List<Vector>();
        var labels = new List<int>();
        var labelNames = new Dictionary<int, string>();

        foreach (var (name, targets, data) in _triggerBuffer.dB)
        {
            if (targets.RowCount == 0) continue;

            // Classification: one class per segment, target rows identical.
            // Take row 0 and argmax across its columns to get the class index.
            int label = ArgMaxRow(targets, 0);
            if (label < 0) continue;

            if (!labelNames.ContainsKey(label))
                labelNames[label] = name;

            for (int r = 0; r < data.RowCount; r++)
            {
                var row = data.Row(r);
                if (row.Count == InputDim)
                {
                    features.Add(row);
                    labels.Add(label);
                }
            }
        }

        if (features.Count == 0)
        {
            Debug.WriteLine($"[{Name}] No valid feature rows from TriggerBuffer.");
            return;
        }

        int maxLabel = labels.Max();
        NumClasses = Math.Max(NumClasses, maxLabel + 1);

        ClassLabels.Clear();
        for (int c = 0; c < NumClasses; c++)
            ClassLabels.Add(labelNames.GetValueOrDefault(c, $"Class {c}"));
        OnLabelsUpdated?.Invoke(ClassLabels);

        _model = ClassifierFactory.Create(ModelType, InputDim, NumClasses, Config);

        switch (_model)
        {
            case LdaClassifier lda:         lda.TrainBatch(features, labels); break;
            case RandomForestClassifier rf:  rf.TrainBatch(features, labels);  break;
            case KnnClassifier knn:          knn.TrainBatch(features, labels); break;
            case ThresholdClassifier tc:     tc.TrainBatch(features, labels);  break;
            case LinearSvmClassifier svm:    svm.TrainBatch(features, labels); break;
            default:
                foreach (var (x, lbl) in features.Zip(labels))
                    _model.Update(x, lbl);
                break;
        }

        SampleCount = features.Count;
        IsTrained = true;

        Debug.WriteLine($"[{Name}] Retrained: {features.Count} samples, {NumClasses} classes, " +
                        $"{_triggerBuffer.dB.Count} segments, {ModelType}");
        OnRetrained?.Invoke(features.Count, NumClasses);
    }

    #endregion

    #region Public API

    public void ChangeModel(ClassifierType type, ClassifierConfig? config = null)
    {
        ModelType = type;
        Config = config ?? Config;
        _model = ClassifierFactory.Create(ModelType, InputDim, NumClasses, Config);
        IsTrained = false;
        SampleCount = 0;

        if (TrainingMode == ClassifierTrainingMode.Buffer && _triggerBuffer?.dB.Count > 0)
            RetrainFromBuffer();

        Debug.WriteLine($"[{Name}] Model changed to {ModelType}");
    }

    public void SetTrainingMode(ClassifierTrainingMode mode)
    {
        TrainingMode = mode;
        Debug.WriteLine($"[{Name}] Training mode → {mode}");

        if (mode == ClassifierTrainingMode.Buffer && _triggerBuffer?.dB.Count > 0)
            RetrainFromBuffer();
    }

    public void ResetModel()
    {
        _model.Reset();
        SampleCount = 0;
        Confidence = 0;
        IsTrained = false;
        IsTraining = false;
        _currentLabel = -1;
        Debug.WriteLine($"[{Name}] Model reset");
    }

    public void ForceRetrain()
    {
        RetrainFromBuffer();
    }

    #endregion

    #region Dispose

    public override void Dispose()
    {
        try { Viz?.Dispose(); } catch { /* no-op */ }
        base.Dispose();
    }

    #endregion
}