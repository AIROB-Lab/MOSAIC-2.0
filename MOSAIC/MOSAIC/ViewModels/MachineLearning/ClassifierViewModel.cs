using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Components.MachineLearning.Factory;
using MOSAIC.Models.MachineLearning;
using MOSAIC.Visualization;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.ViewModels.MachineLearning;

/// <summary>
/// ViewModel for <see cref="ClassifierBlock"/>.
/// Provides real-time classification visualization, model selection,
/// hyperparameter editing, training mode toggle, and training statistics.
/// </summary>
public partial class ClassifierBlockViewModel : ObservableObject, IDisposable
{
    private readonly ClassifierBlock _block;
    private readonly object _lock = new();

    private const int UiUpdateIntervalMs = 100;
    private DateTime _lastUiUpdate = DateTime.MinValue;
    private bool _uiUpdatePending;

    private double _pendingConfidence;
    private int _pendingSampleCount;
    private int _pendingPredictedClass;
    private double[]? _pendingProbabilities;

    #region Observable Properties — Model Selection

    /// <summary>Currently selected classifier type.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedModelDescription))]
    [NotifyPropertyChangedFor(nameof(IsBatchModel))]
    [NotifyPropertyChangedFor(nameof(IsRffModel))]
    [NotifyPropertyChangedFor(nameof(IsKnnModel))]
    [NotifyPropertyChangedFor(nameof(IsForestModel))]
    [NotifyPropertyChangedFor(nameof(IsThresholdModel))]
    private ClassifierType _selectedModelType;

    /// <summary>Current training mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBufferMode))]
    [NotifyPropertyChangedFor(nameof(IsIncrementalMode))]
    private ClassifierTrainingMode _selectedTrainingMode;

    #endregion

    #region Observable Properties — Hyperparameters

    [ObservableProperty] private double _learningRate;
    [ObservableProperty] private double _lambda;
    [ObservableProperty] private double _sigma;
    [ObservableProperty] private int _featureDim;
    [ObservableProperty] private int _k;
    [ObservableProperty] private bool _weightByDistance;
    [ObservableProperty] private int _numTrees;
    [ObservableProperty] private int _maxDepth;
    [ObservableProperty] private int _minSamplesLeaf;
    [ObservableProperty] private double _ldaRegularization;
    [ObservableProperty] private double _confidenceThreshold;

    #endregion

    #region Observable Properties — State

    [ObservableProperty] private bool _isTraining;
    [ObservableProperty] private string _status = "Idle";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfidencePercent))]
    private double _confidence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SampleCountText))]
    private int _sampleCount;

    [ObservableProperty] private int _segmentCount;
    [ObservableProperty] private int _predictedClass;
    [ObservableProperty] private string _predictedClassName = "—";
    [ObservableProperty] private string _currentTargetName = "None";
    [ObservableProperty] private bool _isTrained;
    [ObservableProperty] private bool _isConfigExpanded;
    [ObservableProperty] private bool _isBarsExpanded = true;
    [ObservableProperty] private ObservableCollection<PredictionBarItem> _probabilityBars = new();

    #endregion

    #region Computed Properties

    public ClassifierBlock Block => _block;
    public string Name => _block.Name;
    public int InputDimension => _block.InputDim;
    public int NumClasses => _block.NumClasses;
    public string FrequencyText => $"{_block.DesiredRate:F0} Hz";

    public ObservableCollection<ClassifierType> AvailableModelTypes { get; } = new(Enum.GetValues<ClassifierType>());
    public ObservableCollection<ClassifierTrainingMode> AvailableTrainingModes { get; } = new(Enum.GetValues<ClassifierTrainingMode>());

    public string SelectedModelDescription => ClassifierFactory.GetDescription(SelectedModelType);
    public bool IsBatchModel => ClassifierFactory.IsBatchClassifier(SelectedModelType);
    public bool IsRffModel => SelectedModelType == ClassifierType.SoftmaxRFF;
    public bool IsKnnModel => SelectedModelType == ClassifierType.KNN;
    public bool IsForestModel => SelectedModelType == ClassifierType.RandomForest;
    public bool IsThresholdModel => SelectedModelType == ClassifierType.Threshold;
    public bool IsBufferMode => SelectedTrainingMode == ClassifierTrainingMode.Buffer;
    public bool IsIncrementalMode => SelectedTrainingMode == ClassifierTrainingMode.Incremental;
    public double ConfidencePercent => Math.Clamp(Confidence * 100, 0, 100);
    public string SampleCountText => $"{SampleCount:N0} samples";

    #endregion

    #region Visualization

    public BlockVisualization Viz { get; } = new();
    public ObservableCollection<ScopeLegendItem> ClassLegend { get; } = new();

    #endregion

    #region Constructor

    public ClassifierBlockViewModel(ClassifierBlock block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));

        _selectedModelType = _block.ModelType;
        _selectedTrainingMode = _block.TrainingMode;
        _learningRate = _block.Config.LearningRate;
        _lambda = _block.Config.Lambda;
        _sigma = _block.Config.Sigma;
        _featureDim = _block.Config.FeatureDim;
        _k = _block.Config.K;
        _weightByDistance = _block.Config.WeightByDistance;
        _numTrees = _block.Config.NumTrees;
        _maxDepth = _block.Config.MaxDepth;
        _minSamplesLeaf = _block.Config.MinSamplesLeaf;
        _ldaRegularization = _block.Config.LdaRegularization;
        _confidenceThreshold = _block.Config.ConfidenceThreshold;
        _isTraining = _block.IsTraining;
        _confidence = _block.Confidence;
        _sampleCount = _block.SampleCount;
        _segmentCount = _block.SegmentCount;
        _isTrained = _block.IsTrained;

        _block.Viz = Viz;
        BuildDefaultLabels();

        _block.OnPrediction += HandlePrediction;
        _block.OnRetrained += HandleRetrained;
        _block.OnLabelsUpdated += HandleLabelsUpdated;
        _block.OnTrainingStateChanged += HandleTrainingStateChanged;

        if (_block.ClassLabels.Count > 0)
            HandleLabelsUpdated(_block.ClassLabels);
    }

    #endregion

    #region Labels

    private string[] _classNames = Array.Empty<string>();

    private void BuildDefaultLabels()
    {
        ClassLegend.Clear();
        _classNames = new string[_block.NumClasses];
        for (int i = 0; i < _block.NumClasses; i++)
        {
            _classNames[i] = $"Class {i}";
            ClassLegend.Add(new ScopeLegendItem(_classNames[i], ScopeMonitor.GetChannelColorHex(i)));
        }
    }

    private void SetClassLabels(IReadOnlyList<string> labels)
    {
        ClassLegend.Clear();
        _classNames = new string[labels.Count];
        for (int i = 0; i < labels.Count; i++)
        {
            _classNames[i] = labels[i];
            ClassLegend.Add(new ScopeLegendItem(labels[i], ScopeMonitor.GetChannelColorHex(i)));
        }
    }

    private string GetClassName(int classIndex)
    {
        if (classIndex >= 0 && classIndex < _classNames.Length)
            return _classNames[classIndex];
        return $"Class {classIndex}";
    }

    #endregion

    #region Event Handlers

    private void HandlePrediction(int predictedClass, double[] probabilities, double confidence, int sampleCount)
    {
        lock (_lock)
        {
            _pendingPredictedClass = predictedClass;
            _pendingProbabilities = probabilities;
            _pendingConfidence = confidence;
            _pendingSampleCount = sampleCount;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastUiUpdate).TotalMilliseconds >= UiUpdateIntervalMs && !_uiUpdatePending)
        {
            _uiUpdatePending = true;
            _lastUiUpdate = now;

            Dispatcher.UIThread.Post(() =>
            {
                lock (_lock)
                {
                    PredictedClass = _pendingPredictedClass;
                    PredictedClassName = GetClassName(_pendingPredictedClass);
                    Confidence = _pendingConfidence;
                    SampleCount = _pendingSampleCount;
                    SegmentCount = _block.SegmentCount;
                    IsTrained = _block.IsTrained;
                    _uiUpdatePending = false;

                    if (_pendingProbabilities is not null)
                        UpdateProbabilityBars(_pendingProbabilities);
                }
            }, DispatcherPriority.Background);
        }
    }

    private void UpdateProbabilityBars(double[] probabilities)
    {
        while (ProbabilityBars.Count < probabilities.Length)
        {
            var idx = ProbabilityBars.Count;
            ProbabilityBars.Add(new PredictionBarItem(
                GetClassName(idx), 0, ScopeMonitor.GetChannelColorHex(idx)));
        }
        while (ProbabilityBars.Count > probabilities.Length)
            ProbabilityBars.RemoveAt(ProbabilityBars.Count - 1);

        for (int i = 0; i < probabilities.Length; i++)
            ProbabilityBars[i].Value = probabilities[i];
    }

    private void HandleRetrained(int sampleCount, int numClasses)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SampleCount = sampleCount;
            IsTrained = true;
            Status = $"Trained ({sampleCount} samples, {numClasses} classes)";
        }, DispatcherPriority.Background);
    }

    private void HandleLabelsUpdated(List<string> labels)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SetClassLabels(labels);
            ProbabilityBars.Clear();
        }, DispatcherPriority.Background);
    }

    private void HandleTrainingStateChanged(bool isTraining, string? targetName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsTraining = isTraining;
            CurrentTargetName = targetName ?? "None";
            Status = isTraining ? $"Training: {targetName}" : "Predicting";
        }, DispatcherPriority.Background);
    }

    #endregion

    #region Commands

    [RelayCommand] private void ToggleConfigExpanded() => IsConfigExpanded = !IsConfigExpanded;
    [RelayCommand] private void ToggleBarsExpanded() => IsBarsExpanded = !IsBarsExpanded;

    /// <summary>
    /// Builds a config from current UI values and applies the selected model + mode.
    /// </summary>
    [RelayCommand]
    private void ApplyModel()
    {
        var config = new ClassifierConfig
        {
            LearningRate = LearningRate,
            Lambda = Lambda,
            Sigma = Sigma,
            FeatureDim = FeatureDim,
            K = K,
            WeightByDistance = WeightByDistance,
            NumTrees = NumTrees,
            MaxDepth = MaxDepth,
            MinSamplesLeaf = MinSamplesLeaf,
            LdaRegularization = LdaRegularization,
            ConfidenceThreshold = ConfidenceThreshold
        };

        _block.SetTrainingMode(SelectedTrainingMode);
        _block.ChangeModel(SelectedModelType, config);
        IsTrained = _block.IsTrained;
        Status = _block.IsTrained ? "Model Applied + Retrained" : "Model Applied";
    }

    [RelayCommand]
    private void ResetModel()
    {
        _block.ResetModel();
        SampleCount = 0;
        Confidence = 0;
        IsTrained = false;
        PredictedClassName = "—";
        Status = "Model Reset";
    }

    [RelayCommand]
    private void ForceRetrain()
    {
        _block.ForceRetrain();
        IsTrained = _block.IsTrained;
        Status = _block.IsTrained ? $"Retrained ({_block.SampleCount} samples)" : "No Data";
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        _block.OnPrediction -= HandlePrediction;
        _block.OnRetrained -= HandleRetrained;
        _block.OnLabelsUpdated -= HandleLabelsUpdated;
        _block.OnTrainingStateChanged -= HandleTrainingStateChanged;
        Viz.Dispose();
    }

    #endregion
}