using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Enums;
using MOSAIC.Models.MachineLearning;
using MOSAIC.Visualization;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.ViewModels.MachineLearning;

/// <summary>
/// ViewModel for <see cref="UltrasoundClassifier"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the pattern of <c>IncrementalPredictorViewModel</c> — owns the
/// <see cref="BlockVisualization"/>, throttles hot-path prediction events
/// onto the dispatcher with cached pending state under a lock, and exposes
/// classification-specific UI state on top: a softmax probability bar chart
/// (one bar per class), a predicted-class-name label, and a train-from-buffer
/// command driven by an upstream <see cref="MOSAIC.Models.FlowControl.TriggerBuffer"/>.
/// </para>
/// </remarks>
public partial class UltrasoundClassifierViewModel : ObservableObject, IDisposable
{
    private readonly UltrasoundClassifier _block;
    private readonly object _lockObject = new();

    /// <summary>Minimum interval between UI thread dispatches (ms).</summary>
    private const int UiUpdateIntervalMs = 50;

    /// <summary>Default class names if none provided externally.</summary>
    private static readonly IReadOnlyList<string> DefaultClassNames =
        new[] { "THUMB", "INDEX", "MIDDLE", "RING", "PINKY" };

    private DateTime _lastUiUpdate = DateTime.MinValue;
    private bool _uiUpdatePending;

    // Cached values for throttled UI dispatch — set on hot path, applied on UI thread
    private Vector<double>? _pendingProbs;
    private int _pendingPredictedClass;
    private double _pendingPredictedConfidence;

    // ── Direct model exposure ────────────────────────────────────────

    /// <summary>Direct access to the underlying block — used by AXAML bindings
    /// like <c>{Binding UltrasoundClassifier.Status}</c> so the View can
    /// read model state without the VM needing to mirror every property.</summary>
    public UltrasoundClassifier UltrasoundClassifier => _block;

    /// <summary>Direct access to the underlying block.</summary>
    public UltrasoundClassifier Block => _block;

    /// <summary>Block name (computed, no need to mirror).</summary>
    public string Name => _block.Name;

    /// <summary>Formatted desired rate string.</summary>
    public string FrequencyText => $"{_block.DesiredRate:F0} Hz";

    // ── Observable State ─────────────────────────────────────────────

    /// <summary>Most recently predicted class index (argmax of softmax).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PredictedClassName))]
    private int _predictedClass;

    /// <summary>Softmax probability of the predicted class.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PredictedConfidencePercent))]
    private double _predictedConfidence;

    /// <summary>Total predictions so far (mirrors block).</summary>
    [ObservableProperty] private long _predictionCount;

    /// <summary>Block status (mirrored).</summary>
    [ObservableProperty] private BlockStatus _status;

    /// <summary>True while the model is currently training.</summary>
    [ObservableProperty] private bool _isTraining;

    /// <summary>True if the model has been trained or loaded from a checkpoint.</summary>
    [ObservableProperty] private bool _isTrained;

    /// <summary>True when the underlying model is configured as a regression
    /// predictor (independent sigmoid outputs) instead of a classifier
    /// (mutually-exclusive softmax). Mirrors the block's mode so the AXAML
    /// can toggle badges, stat labels, and bar interpretations without
    /// reaching into the model directly.</summary>
    [ObservableProperty] private bool _isRegression;

    /// <summary>Number of labelled segments accumulated by the upstream TriggerBuffer.</summary>
    [ObservableProperty] private int _bufferSegmentCount;

    /// <summary>Status message for the training panel.</summary>
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Model description (e.g. "ResNet-18 · 5 classes · 224×224").</summary>
    [ObservableProperty] private string _modelDescription = string.Empty;

    // ── Hyperparameters (owned by VM, edited via UI) ─────────────────

    [ObservableProperty] private int    _trainEpochs  = 10;
    [ObservableProperty] private int    _batchSize    = 16;
    [ObservableProperty] private double _learningRate = 1e-4;
    [ObservableProperty] private double _weightDecay  = 1e-5;
    [ObservableProperty] private double _gradientClip = 1.0;

    // ── UI state ─────────────────────────────────────────────────────

    /// <summary>Whether the model-configuration section is expanded.</summary>
    [ObservableProperty] private bool _isConfigExpanded;

    /// <summary>Whether the live prediction bar chart is expanded.</summary>
    [ObservableProperty] private bool _isBarsExpanded = true;

    /// <summary>Live softmax probability bars, one per class.</summary>
    [ObservableProperty]
    private ObservableCollection<PredictionBarItem> _predictionBars = new();

    // ── Computed properties ──────────────────────────────────────────

    /// <summary>Class name for the currently predicted class index.</summary>
    public string PredictedClassName =>
        PredictedClass >= 0 && PredictedClass < PredictionLabels.Count
            ? PredictionLabels[PredictedClass].Label
            : PredictedClass.ToString();

    /// <summary>Confidence as a 0–100 % value for display.</summary>
    public double PredictedConfidencePercent => Math.Clamp(PredictedConfidence * 100.0, 0.0, 100.0);

    // ── Visualization ────────────────────────────────────────────────

    /// <summary>
    /// Visualization bundle. The VM owns it and assigns it onto the block at
    /// construction so the block's <c>Predict</c> can feed it directly.
    /// </summary>
    public BlockVisualization Viz { get; } = new();

    /// <summary>
    /// Color-coded legend entries matching prediction-bar colors to class names.
    /// Colors come from <see cref="ScopeMonitor.GetChannelColorHex"/> so they
    /// stay consistent with any scope traces the user may add downstream.
    /// </summary>
    public ObservableCollection<ScopeLegendItem> PredictionLabels { get; } = new();

    // ══════════════════════════════════════════════════════════════════
    //  Construction
    // ══════════════════════════════════════════════════════════════════

    public UltrasoundClassifierViewModel(UltrasoundClassifier block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));

        // Initial state mirror.
        _status              = _block.Status;
        _isTraining          = _block.IsTraining;
        _isTrained           = _block.IsTrained;
        _isRegression        = _block.IsRegression;
        _predictionCount     = _block.PredictionCount;
        _predictedClass      = _block.PredictedClass;
        _predictedConfidence = _block.PredictedConfidence;
        _modelDescription    = _block.ModelDescription;
        _learningRate        = _block.LearningRate;

        // VM owns the visualization, attach it to the block so Predict can feed it.
        _block.Viz = Viz;

        BuildDefaultLabels(_block.NumClasses);

        _block.OnPrediction           += HandlePrediction;
        _block.OnTrainingStateChanged += HandleTrainingStateChanged;
        _block.OnBufferSegmentAdded   += HandleBufferSegmentAdded;
    }

    // ══════════════════════════════════════════════════════════════════
    //  Labels
    // ══════════════════════════════════════════════════════════════════

    private void BuildDefaultLabels(int classCount)
    {
        PredictionLabels.Clear();
        for (int i = 0; i < classCount; i++)
        {
            var label = i < DefaultClassNames.Count ? DefaultClassNames[i] : $"Class[{i}]";
            PredictionLabels.Add(new ScopeLegendItem(label, ScopeMonitor.GetChannelColorHex(i)));
        }
        OnPropertyChanged(nameof(PredictedClassName));
    }

    /// <summary>
    /// Override the default class labels (e.g. with names from a Trigger block
    /// or for a model with a different class set).
    /// </summary>
    public void SetPredictionLabels(IReadOnlyList<string> names)
    {
        PredictionLabels.Clear();
        for (int i = 0; i < names.Count; i++)
            PredictionLabels.Add(new ScopeLegendItem(names[i], ScopeMonitor.GetChannelColorHex(i)));

        // Existing bars need to be rebuilt so their labels match the new legend.
        PredictionBars.Clear();
        OnPropertyChanged(nameof(PredictedClassName));
    }

    // ══════════════════════════════════════════════════════════════════
    //  Event handlers — hot path
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Receives every softmax probability vector from the block. Records the
    /// snapshot under a lock; the throttled UI dispatch reads it on the
    /// dispatcher thread so the bar chart and stats stay in sync without
    /// flooding Avalonia with redraws.
    /// </summary>
    private void HandlePrediction(Vector<double> probs)
    {
        lock (_lockObject)
        {
            _pendingProbs = probs;

            // argmax + confidence for the header label.
            var values = probs.AsArray() ?? probs.ToArray();
            int best = 0;
            double bestVal = values.Length > 0 ? values[0] : 0.0;
            for (int i = 1; i < values.Length; i++)
            {
                if (values[i] > bestVal) { bestVal = values[i]; best = i; }
            }
            _pendingPredictedClass      = best;
            _pendingPredictedConfidence = bestVal;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastUiUpdate).TotalMilliseconds >= UiUpdateIntervalMs && !_uiUpdatePending)
        {
            _uiUpdatePending = true;
            _lastUiUpdate    = now;

            Dispatcher.UIThread.Post(() =>
            {
                Vector<double>? probsSnapshot;
                int     classSnapshot;
                double  confSnapshot;
                lock (_lockObject)
                {
                    probsSnapshot   = _pendingProbs;
                    classSnapshot   = _pendingPredictedClass;
                    confSnapshot    = _pendingPredictedConfidence;
                    _uiUpdatePending = false;
                }

                PredictedClass      = classSnapshot;
                PredictedConfidence = confSnapshot;
                PredictionCount     = _block.PredictionCount;

                if (probsSnapshot is not null)
                    UpdatePredictionBars(probsSnapshot);
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Sync <see cref="PredictionBars"/> with the latest probability vector.
    /// Grows or shrinks the collection in place when the class count changes
    /// (avoids tearing down the whole ItemsControl on every prediction).
    /// </summary>
    private void UpdatePredictionBars(Vector<double> probs)
    {
        while (PredictionBars.Count < probs.Count)
        {
            int idx = PredictionBars.Count;
            string label = idx < PredictionLabels.Count
                           ? PredictionLabels[idx].Label
                           : $"Class[{idx}]";
            PredictionBars.Add(new PredictionBarItem(
                label, 0.0, ScopeMonitor.GetChannelColorHex(idx)));
        }
        while (PredictionBars.Count > probs.Count)
            PredictionBars.RemoveAt(PredictionBars.Count - 1);

        for (int i = 0; i < probs.Count; i++)
        {
            var v = probs[i];
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0.0;
            PredictionBars[i].Value = Math.Clamp(v, 0.0, 1.0);
        }
    }

    private void HandleTrainingStateChanged(bool training)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsTraining = training;
            Status     = _block.Status;
            IsTrained  = _block.IsTrained;
            TrainCommand.NotifyCanExecuteChanged();
            SaveModelCommand.NotifyCanExecuteChanged();
            LoadModelCommand.NotifyCanExecuteChanged();
        }, DispatcherPriority.Background);
    }

    private void HandleBufferSegmentAdded()
    {
        Dispatcher.UIThread.Post(() =>
        {
            BufferSegmentCount = _block.Buffer?.dB.Count ?? 0;
            TrainCommand.NotifyCanExecuteChanged();
        }, DispatcherPriority.Background);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Commands
    // ══════════════════════════════════════════════════════════════════

    [RelayCommand] private void ToggleConfigExpanded() => IsConfigExpanded = !IsConfigExpanded;
    [RelayCommand] private void ToggleBarsExpanded()   => IsBarsExpanded   = !IsBarsExpanded;

    [RelayCommand(CanExecute = nameof(CanTrain))]
    private async Task TrainAsync()
    {
        if (_block.Buffer is null) { StatusMessage = "No trigger buffer connected."; return; }
        var clusters = _block.Buffer.dB;
        if (clusters.Count == 0)   { StatusMessage = "Buffer is empty.";              return; }

        StatusMessage = $"Training on {clusters.Count} segments…";
        try
        {
            await Task.Run(() => _block.TrainIncremental(
                clusters,
                epochs:       TrainEpochs,
                batchSize:    BatchSize,
                lr:           LearningRate,
                weightDecay:  WeightDecay,
                gradientClip: GradientClip));
            StatusMessage = "Training complete.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Training failed: {ex.Message}";
        }
    }

    private bool CanTrain() => !IsTraining;

    [RelayCommand]
    private void ResetModel()
    {
        _block.ResetModel();
        IsTrained     = false;
        StatusMessage = "Model reset to random init.";
    }

    [RelayCommand]
    private void ResetToCheckpoint()
    {
        _block.ResetToCheckpoint();
        IsTrained = !string.IsNullOrEmpty(_block.ModelPath);
        StatusMessage = IsTrained
                        ? "Model reloaded from checkpoint."
                        : "No checkpoint configured — reset to random instead.";
    }

    /// <summary>
    /// Path bound to the Save/Load textbox. Two-way synced with the block's
    /// ModelPath so opening a saved file updates the field and editing the
    /// field updates the block's understanding of where to save next.
    /// </summary>
    public string ModelPath
    {
        get => _block.ModelPath;
        set
        {
            if (_block.ModelPath == value) return;
            _block.ModelPath = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Save the current trained state to <see cref="ModelPath"/>. Disabled
    /// while training is in progress to avoid racing the Python save inside
    /// <c>train_incremental</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveModel))]
    private void SaveModel()
    {
        if (string.IsNullOrWhiteSpace(ModelPath))
        {
            StatusMessage = "Set a model path before saving.";
            return;
        }
        _block.SaveModel(ModelPath);
        StatusMessage = $"Model saved → {ModelPath}";
    }

    private bool CanSaveModel() => !IsTraining;

    /// <summary>
    /// Load a previously-saved state_dict from <see cref="ModelPath"/>. Sets
    /// IsTrained so downstream UI (predicted bars, "ready" badge) reflects that
    /// inference can run.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadModel))]
    private void LoadModel()
    {
        if (string.IsNullOrWhiteSpace(ModelPath))
        {
            StatusMessage = "Set a model path before loading.";
            return;
        }
        _block.LoadModel(ModelPath);
        IsTrained     = _block.IsTrained;
        StatusMessage = IsTrained
                        ? $"Model loaded ← {ModelPath}"
                        : $"Load failed — see console.";
    }

    private bool CanLoadModel() => !IsTraining;

    // ══════════════════════════════════════════════════════════════════
    //  IDisposable
    // ══════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        _block.OnPrediction           -= HandlePrediction;
        _block.OnTrainingStateChanged -= HandleTrainingStateChanged;
        _block.OnBufferSegmentAdded   -= HandleBufferSegmentAdded;
        Viz.Dispose();
    }
}