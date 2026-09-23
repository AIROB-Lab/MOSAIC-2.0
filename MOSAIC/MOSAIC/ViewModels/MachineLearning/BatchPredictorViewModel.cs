using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.MachineLearning.Factory;
using MOSAIC.Models.MachineLearning;
using MOSAIC.Visualization;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.ViewModels.MachineLearning;

/// <summary>
/// ViewModel for <see cref="BatchPredictor"/>.
/// </summary>
public partial class BatchPredictorViewModel : ObservableObject, IDisposable
{
    private readonly BatchPredictor _block;
    private readonly object _lockObject = new();

    private const int UiUpdateIntervalMs = 100;

    private DateTime _lastUiUpdate = DateTime.MinValue;
    private bool _uiUpdatePending;

    private double _pendingConfidence;
    private int _pendingSampleCount;
    private Vector<double>? _pendingPrediction;

    // ── Observable State ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedModelDescription))]
    [NotifyPropertyChangedFor(nameof(IsRffModel))]
    private RegressorType _selectedModelType;

    [ObservableProperty] private double _lambda;
    [ObservableProperty] private double _sigma;
    [ObservableProperty] private int _featureDim;
    [ObservableProperty] private bool _isTraining;
    [ObservableProperty] private bool _isTrained;
    [ObservableProperty] private string _status = "Idle";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfidencePercent))]
    private double _confidence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SampleCountText))]
    private int _sampleCount;

    [ObservableProperty] private int _segmentsProcessed;
    [ObservableProperty] private string _currentTargetName = "None";
    [ObservableProperty] private bool _isConfigExpanded;
    [ObservableProperty] private bool _isBarsExpanded;
    [ObservableProperty] private ObservableCollection<PredictionBarItem> _predictionBars = new();
    [ObservableProperty] private string _loadPath = string.Empty;
    [ObservableProperty] private int _bufferSegmentCount;

    // ── Computed Properties ──

    public BatchPredictor Block => _block;
    public string Name => _block.Name;
    public int InputDimension => _block.InputDim;
    public int OutputDimension => _block.OutputDim;
    public string FrequencyText => $"{_block.DesiredRate:F0} Hz";

    public ObservableCollection<RegressorType> AvailableModelTypes { get; } = new(Enum.GetValues<RegressorType>());

    public string SelectedModelDescription => RegressorFactory.GetDescription(SelectedModelType);
    public bool IsRffModel => SelectedModelType == RegressorType.RidgeRFF;
    public double ConfidencePercent => Math.Min(100, Math.Max((double)0, 100 - Confidence * 10));
    public string SampleCountText => $"{SampleCount:N0} samples";

    public bool ResetBeforeTraining
    {
        get => _block.ResetBeforeTraining;
        set
        {
            if (_block.ResetBeforeTraining == value) return;
            _block.ResetBeforeTraining = value;
            OnPropertyChanged();
        }
    }

    public BlockVisualization Viz { get; } = new();
    public ObservableCollection<ScopeLegendItem> PredictionLabels { get; } = new();

    // ══════════════════════════════════════════════════════
    //  Construction
    // ══════════════════════════════════════════════════════

    public BatchPredictorViewModel(BatchPredictor block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));

        _selectedModelType  = _block.ModelType;
        _lambda             = _block.Config.Lambda;
        _sigma              = _block.Config.Sigma;
        _featureDim         = _block.Config.FeatureDim;
        _isTraining         = _block.IsTraining;
        _isTrained          = _block.IsTrained;
        _confidence         = _block.Confidence;
        _sampleCount        = _block.SampleCount;
        _segmentsProcessed  = _block.SegmentsProcessed;
        _currentTargetName  = _block.CurrentTargetName;
        _bufferSegmentCount = _block.Buffer?.dB.Count ?? 0;
        _status             = _block.IsTraining ? "Training…" : "Idle";

        BuildDefaultLabels();

        _block.OnPrediction           += HandlePrediction;
        _block.OnTrainingStateChanged += HandleTrainingStateChanged;
        _block.OnBufferSegmentAdded   += HandleBufferSegmentAdded;

        if (_block.Buffer is not null && _block.Buffer.dB.Count > 0)
            HandleBufferSegmentAdded();
    }

    // ══════════════════════════════════════════════════════
    //  Labels
    // ══════════════════════════════════════════════════════

    private void BuildDefaultLabels()
    {
        PredictionLabels.Clear();
        var names = GetDefaultOutputNames(_block.OutputDim);
        for (int i = 0; i < names.Length; i++)
            PredictionLabels.Add(new ScopeLegendItem(names[i], ScopeMonitor.GetChannelColorHex(i)));
    }

    public void SetPredictionLabels(IReadOnlyList<string> names)
    {
        PredictionLabels.Clear();
        for (int i = 0; i < names.Count; i++)
            PredictionLabels.Add(new ScopeLegendItem(names[i], ScopeMonitor.GetChannelColorHex(i)));
    }

    private static string[] GetDefaultOutputNames(int outputDim)
    {
        var names = new string[outputDim];
        for (int i = 0; i < outputDim; i++)
            names[i] = $"Out[{i}]";
        return names;
    }

    /// <summary>
    /// Pick one display label per output column by scanning the buffer's segments
    /// and choosing whichever segment's targets matrix has the largest absolute
    /// value in that column. Reads target row 0 — for classification all rows are
    /// identical, for streaming the start-of-segment label is a reasonable choice.
    /// Segments named "rest" are skipped so they don't claim every column.
    /// </summary>
    private List<string> DeriveLabelsFromBuffer()
    {
        var labels = new List<string>(GetDefaultOutputNames(_block.OutputDim));
        var buffer = _block.Buffer;
        if (buffer is null) return labels;

        var snapshot = buffer.dB.ToList();
        for (int col = 0; col < _block.OutputDim; col++)
        {
            string bestLabel = labels[col];
            double bestValue = 0;

            foreach (var (label, targets, _) in snapshot)
            {
                if (string.Equals(label, "rest", StringComparison.OrdinalIgnoreCase)) continue;
                if (targets.RowCount == 0 || col >= targets.ColumnCount) continue;

                double v = Math.Abs(targets[0, col]);
                if (v > bestValue)
                {
                    bestValue = v;
                    bestLabel = label;
                }
            }

            labels[col] = bestLabel;
        }

        return labels;
    }

    // ══════════════════════════════════════════════════════
    //  Event Handlers (hot path)
    // ══════════════════════════════════════════════════════

    private void HandlePrediction(Vector<double> prediction, Vector<double>? target, double confidence, int sampleCount)
    {
        lock (_lockObject)
        {
            _pendingConfidence  = confidence;
            _pendingSampleCount = sampleCount;
            _pendingPrediction  = prediction;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastUiUpdate).TotalMilliseconds >= UiUpdateIntervalMs && !_uiUpdatePending)
        {
            _uiUpdatePending = true;
            _lastUiUpdate    = now;

            Dispatcher.UIThread.Post(() =>
            {
                lock (_lockObject)
                {
                    Confidence       = _pendingConfidence;
                    SampleCount      = _pendingSampleCount;
                    _uiUpdatePending = false;

                    if (_pendingPrediction is not null)
                        UpdatePredictionBars(_pendingPrediction);
                }
            }, DispatcherPriority.Background);
        }
    }

    private void UpdatePredictionBars(Vector<double> prediction)
    {
        var labels = DeriveLabelsFromBuffer();

        while (PredictionBars.Count < prediction.Count)
        {
            var idx = PredictionBars.Count;
            var label = idx < labels.Count ? labels[idx] : $"Out[{idx}]";
            PredictionBars.Add(new PredictionBarItem(
                label, 0, ScopeMonitor.GetChannelColorHex(idx)));
        }
        while (PredictionBars.Count > prediction.Count)
            PredictionBars.RemoveAt(PredictionBars.Count - 1);

        for (int i = 0; i < prediction.Count; i++)
        {
            var desired = i < labels.Count ? labels[i] : $"Out[{i}]";
            if (PredictionBars[i].Label != desired)
                PredictionBars[i] = new PredictionBarItem(desired, prediction[i], ScopeMonitor.GetChannelColorHex(i));
            else
                PredictionBars[i].Value = prediction[i];
        }
    }

    private void HandleBufferSegmentAdded()
    {
        Dispatcher.UIThread.Post(() =>
        {
            BufferSegmentCount = _block.Buffer?.dB.Count ?? 0;

            var labels = DeriveLabelsFromBuffer();
            SetPredictionLabels(labels);

            PredictionBars.Clear();
            Status = $"Buffer: {BufferSegmentCount} segments captured";
        }, DispatcherPriority.Background);
    }

    private void HandleTrainingStateChanged(bool isTraining)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsTraining = isTraining;
            if (isTraining)
            {
                Status = "Training…";
            }
            else
            {
                IsTrained         = _block.IsTrained;
                SampleCount       = _block.SampleCount;
                SegmentsProcessed = _block.SegmentsProcessed;
                CurrentTargetName = _block.CurrentTargetName;
                Status            = $"Trained — {SegmentsProcessed} segments, {SampleCount:N0} samples";
            }
        }, DispatcherPriority.Background);
    }

    // ══════════════════════════════════════════════════════
    //  Commands
    // ══════════════════════════════════════════════════════

    [RelayCommand]
    private async Task TrainFromBufferAsync()
    {
        if (_block.Buffer is null)
        {
            Status = "No buffer wired — connect a TriggerBuffer to this block's inputs.";
            return;
        }
        if (BufferSegmentCount == 0)
        {
            Status = "Buffer is empty — capture at least one segment via the Trigger card first.";
            return;
        }
        if (_block.IsTraining)
        {
            Status = "Already training.";
            return;
        }

        try
        {
            await Task.Run(() => _block.TrainFromBuffer()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Training failed: {ex.Message}";
        }
    }

    [RelayCommand] private void ToggleConfigExpanded() => IsConfigExpanded = !IsConfigExpanded;
    [RelayCommand] private void ToggleBarsExpanded() => IsBarsExpanded = !IsBarsExpanded;

    [RelayCommand]
    private void ApplyModel()
    {
        _block.ChangeModel(SelectedModelType, new RegressorConfig
        {
            Lambda = Lambda, Sigma = Sigma, FeatureDim = FeatureDim
        });
        ResetStats();
        Status = "Model Applied";
    }

    [RelayCommand]
    private void ResetModel()
    {
        _block.ResetModel();
        ResetStats();
        Status = "Model Reset";
    }

    [RelayCommand]
    private void SaveModel()
    {
        try   { _block.SaveModel(); Status = "Model Saved"; }
        catch { Status = "Save Failed"; }
    }

    [RelayCommand]
    private void LoadModel()
    {
        if (string.IsNullOrWhiteSpace(LoadPath) || !File.Exists(LoadPath))
        {
            Status = "Load path is empty or file not found.";
            return;
        }

        try
        {
            _block.LoadModel(LoadPath);
            IsTrained = _block.IsTrained;
            Status    = $"Loaded {Path.GetFileName(LoadPath)}.";
        }
        catch (Exception ex)
        {
            Status = $"Load failed: {ex.Message}";
        }
    }

    private void ResetStats()
    {
        lock (_lockObject)
        {
            _pendingSampleCount = 0;
            _pendingConfidence  = 0;
        }
        SampleCount       = 0;
        SegmentsProcessed = 0;
        Confidence        = 0;
        IsTrained         = false;
    }

    public void Dispose()
    {
        _block.OnPrediction           -= HandlePrediction;
        _block.OnTrainingStateChanged -= HandleTrainingStateChanged;
        _block.OnBufferSegmentAdded   -= HandleBufferSegmentAdded;
        Viz.Dispose();
    }
}