using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Enums;
using MOSAIC.Components.MachineLearning.Factory;
using MOSAIC.Models.MachineLearning;
using MOSAIC.Visualization;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.ViewModels.MachineLearning;

/// <summary>
/// ViewModel for <see cref="IncrementalPredictor.OnPrediction"/>.
/// Provides real-time prediction visualization, model configuration,
/// and training statistics for the predictor card UI.
/// </summary>
/// <remarks>
/// <para>Subscribes to <see cref="IncrementalPredictor"/> and
/// <see cref="IncrementalPredictor"/> events.
/// UI updates are throttled to <see cref="BlockVisualization"/> to prevent flooding
/// the Avalonia dispatcher.</para>
/// <para>Exposes a <see cref="PredictionLabels"/> for the scope/spider/heatmap panel,
/// color-coded <see cref="IncrementalPredictorViewModel.PredictionBars"/> for the legend, and a live
/// <see cref="IncrementalPredictor"/> collection for the horizontal bar chart.</para>
/// </remarks>
public partial class IncrementalPredictorViewModel : ObservableObject, IDisposable
{
    private readonly IncrementalPredictor _block;
    private readonly object _lockObject = new();
    private readonly Queue<double> _recentErrors = new();

    /// <summary>Number of recent errors to average for the rolling error metric.</summary>
    private const int RecentErrorWindow = 50;

    /// <summary>Minimum interval between UI thread dispatches (ms).</summary>
    private const int UiUpdateIntervalMs = 100;

    private int _tickCount;
    private DateTime _lastUiUpdate = DateTime.MinValue;
    private bool _uiUpdatePending;

    // Cached values for throttled UI dispatch
    private double _pendingConfidence;
    private int _pendingSampleCount;
    private double _pendingCurrentError;
    private double _pendingAverageError;
    private Vector<double>? _pendingPrediction;

    // ── Observable State ──

    /// <summary>Currently selected regression model type (Ridge, RidgeRFF, etc.).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedModelDescription))]
    [NotifyPropertyChangedFor(nameof(IsRffModel))]
    private RegressorType _selectedModelType;

    /// <summary>Regularization parameter λ for the regressor.</summary>
    [ObservableProperty] private double _lambda;

    /// <summary>RBF kernel width σ (only used by RFF-based models).</summary>
    [ObservableProperty] private double _sigma;

    /// <summary>Random Fourier Feature dimension D (only used by RFF-based models).</summary>
    [ObservableProperty] private int _featureDim;

    /// <summary>Most recent prediction error (L2 norm of prediction − target).</summary>
    [ObservableProperty] private double _currentError;

    /// <summary>Rolling average error over the last <see cref="RecentErrorWindow"/> samples.</summary>
    [ObservableProperty] private double _averageError;

    /// <summary>Whether the block is currently in training mode (driven by Trigger).</summary>
    [ObservableProperty] private bool _isTraining;

    /// <summary>Human-readable status text shown in the card badge.</summary>
    [ObservableProperty] private string _status = "Idle";

    /// <summary>Raw confidence value from the regressor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfidencePercent))]
    private double _confidence;

    /// <summary>Total number of training samples processed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SampleCountText))]
    private int _sampleCount;

    /// <summary>Name of the current training target (e.g. "power", "flex").</summary>
    [ObservableProperty] private string _currentTargetName = "None";

    /// <summary>Whether the model configuration section is expanded in the UI.</summary>
    [ObservableProperty] private bool _isConfigExpanded;

    /// <summary>Whether the live prediction bar chart is expanded in the UI.</summary>
    [ObservableProperty] private bool _isBarsExpanded;

    /// <summary>Live prediction values for the horizontal bar chart.</summary>
    [ObservableProperty] private ObservableCollection<PredictionBarItem> _predictionBars = new();
    
    // ── Collapse ───────────────────────────────────────────────────────
    [ObservableProperty] private bool _isJsonConfigExpanded = false;
    
    [RelayCommand]
    private void ToggleJsonConfigExpanded() => IsJsonConfigExpanded = !IsJsonConfigExpanded;

    
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrequencyText))]
    private double _desiredRate;


    [ObservableProperty] private int _inputDimension;
    [ObservableProperty] private int _outputDimension;
    
    partial void OnInputDimensionChanged(int value)  => _block.InputDim  = value;
    partial void OnOutputDimensionChanged(int value) => _block.OutputDim = value;

    // ── Computed Properties ──

    /// <summary>The underlying model block.</summary>
    public IncrementalPredictor Block => _block;

    /// <summary>Block name for display.</summary>
    public string Name => _block.Name;
    

    /// <summary>Formatted desired rate string (e.g. "200 Hz").</summary>
    public string FrequencyText => $"{_block.DesiredRate:F0} Hz";

    /// <summary>All available regressor types for the model selector combo box.</summary>
    public ObservableCollection<RegressorType> AvailableModelTypes { get; } = new(Enum.GetValues<RegressorType>());

    /// <summary>Human-readable description of the currently selected model type.</summary>
    public string SelectedModelDescription => RegressorFactory.GetDescription(SelectedModelType);

    /// <summary>Whether the selected model uses Random Fourier Features (enables σ and D sliders).</summary>
    public bool IsRffModel => SelectedModelType == RegressorType.RidgeRFF;

    /// <summary>Confidence mapped to a 0–100% scale for display.</summary>
    public double ConfidencePercent => Math.Min(100, Math.Max((double)0, 100 - Confidence * 10));

    /// <summary>Formatted sample count (e.g. "1,234 samples").</summary>
    public string SampleCountText => $"{SampleCount:N0} samples";

    // ── Visualization ──

    /// <summary>
    /// Visualization bundle providing scope, spider, and heatmap monitors.
    /// Bound in XAML via <c>&lt;viz:VisualizationPanel Source="{Binding Viz}"/&gt;</c>.
    /// </summary>
    public BlockVisualization Viz { get; } = new();

    /// <summary>
    /// Color-coded legend items matching scope trace colors to prediction output names.
    /// Populated from <see cref="GetDefaultOutputNames"/> or overridden via <see cref="SetPredictionLabels"/>.
    /// </summary>
    public ObservableCollection<ScopeLegendItem> PredictionLabels { get; } = new();

    // ══════════════════════════════════════════════════════
    //  Construction
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// Initializes a new <see cref="IncrementalPredictorViewModel"/> and wires it
    /// to the given <paramref name="block"/>.
    /// </summary>
    /// <param name="block">The predictor model block to bind to.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="block"/> is <c>null</c>.</exception>
    public IncrementalPredictorViewModel(IncrementalPredictor block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));

        _selectedModelType = _block.ModelType;
        _lambda = _block.Config.Lambda;
        _sigma = _block.Config.Sigma;
        _featureDim = _block.Config.FeatureDim;
        _isTraining = _block.IsTraining;
        _confidence = _block.Confidence;
        _sampleCount = _block.SampleCount;
        _currentTargetName = _block.CurrentTargetName;
        _status = _block.IsTraining ? $"Training: {_currentTargetName}" : "Idle";
        _desiredRate = _block.DesiredRate;
        _inputDimension = _block.InputDim;
        _outputDimension = _block.OutputDim;

        _block.Viz = Viz;
        BuildDefaultLabels();

        _block.OnPrediction += HandlePrediction;
        _block.OnTrainingStateChanged += HandleTrainingStateChanged;
        _block.OnLabelsUpdated += HandleLabelsUpdated;

        // If labels were already extracted (e.g. loaded model), apply them now
        if (_block.OutputLabels is not null)
            HandleLabelsUpdated(_block.OutputLabels);
    }

    // ══════════════════════════════════════════════════════
    //  Labels
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// Populates <see cref="PredictionLabels"/> with default output names
    /// and matching scope trace colors.
    /// </summary>
    private void BuildDefaultLabels()
    {
        PredictionLabels.Clear();
        var names = GetDefaultOutputNames(_block.OutputDim);
        for (int i = 0; i < names.Length; i++)
            PredictionLabels.Add(new ScopeLegendItem(names[i], ScopeMonitor.GetChannelColorHex(i)));
    }

    /// <summary>
    /// Overrides the default prediction labels with custom names
    /// (e.g. action names from a <c>Trigger</c> block).
    /// </summary>
    /// <param name="names">Ordered list of output channel names.</param>
    public void SetPredictionLabels(IReadOnlyList<string> names)
    {
        PredictionLabels.Clear();
        for (int i = 0; i < names.Count; i++)
            PredictionLabels.Add(new ScopeLegendItem(names[i], ScopeMonitor.GetChannelColorHex(i)));
    }

    /// <summary>
    /// Returns default output names as generic indices.
    /// Trigger-derived labels override these via <see cref="SetPredictionLabels"/>.
    /// </summary>
    /// <param name="outputDim">Number of output dimensions.</param>
    /// <returns>Array of <paramref name="outputDim"/> label strings.</returns>
    private static string[] GetDefaultOutputNames(int outputDim)
    {
        var names = new string[outputDim];
        for (int i = 0; i < outputDim; i++)
            names[i] = $"Out[{i}]";
        return names;
    }

    // ══════════════════════════════════════════════════════
    //  Event Handlers (hot path)
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// Handles each prediction from the block. Feeds the visualization,
    /// caches statistics, and dispatches throttled UI updates.
    /// </summary>
    private void HandlePrediction(Vector<double> prediction, Vector<double>? target, double confidence, int sampleCount)
    {
        _tickCount++;
        // NOTE: Viz.Feed() is called by the block in HandleFeatureInput — don't double-feed here

        lock (_lockObject)
        {
            _pendingConfidence = confidence;
            _pendingSampleCount = sampleCount;
            _pendingPrediction = prediction;

            if (target is not null)
            {
                var error = (prediction - target).L2Norm();
                _pendingCurrentError = error;

                _recentErrors.Enqueue(error);
                if (_recentErrors.Count > RecentErrorWindow)
                    _recentErrors.Dequeue();

                _pendingAverageError = _recentErrors.Average();
            }
        }

        var now = DateTime.UtcNow;
        if ((now - _lastUiUpdate).TotalMilliseconds >= UiUpdateIntervalMs && !_uiUpdatePending)
        {
            _uiUpdatePending = true;
            _lastUiUpdate = now;

            Dispatcher.UIThread.Post(() =>
            {
                lock (_lockObject)
                {
                    Confidence = _pendingConfidence;
                    SampleCount = _pendingSampleCount;
                    CurrentError = _pendingCurrentError;
                    AverageError = _pendingAverageError;
                    _uiUpdatePending = false;

                    if (_pendingPrediction is not null)
                        UpdatePredictionBars(_pendingPrediction);
                }
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Synchronizes <see cref="IncrementalPredictorViewModel.PredictionBars"/> with the latest prediction vector.
    /// Resizes the collection if the output dimension changes, then updates values in-place.
    /// </summary>
    private void UpdatePredictionBars(Vector<double> prediction)
    {
        // Prefer labels from Trigger, fall back to defaults
        var labels = _block.OutputLabels ?? new List<string>(GetDefaultOutputNames(_block.OutputDim));

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
            PredictionBars[i].Value = prediction[i];
    }

    /// <summary>
    /// Handles label updates from the <see cref="IncrementalPredictor"/>
    /// when Trigger action names become available. Updates both the legend and bar labels.
    /// </summary>
    private void HandleLabelsUpdated(List<string> labels)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SetPredictionLabels(labels);

            // Also update existing bar items
            PredictionBars.Clear();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Handles training state changes dispatched from the <see cref="IncrementalPredictor"/>.
    /// Updates <see cref="IncrementalPredictorViewModel.IsTraining"/>, <see cref="IncrementalPredictorViewModel.CurrentTargetName"/>, and <see cref="IncrementalPredictorViewModel.Status"/>.
    /// </summary>
    private void HandleTrainingStateChanged(bool isTraining, string? targetName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsTraining = isTraining;
            CurrentTargetName = targetName ?? "None";
            Status = isTraining ? $"Training: {targetName}" : "Idle";
        }, DispatcherPriority.Background);
    }

    // ══════════════════════════════════════════════════════
    //  Commands
    // ══════════════════════════════════════════════════════

    /// <summary>Toggles the model configuration section visibility.</summary>
    [RelayCommand] private void ToggleConfigExpanded() => IsConfigExpanded = !IsConfigExpanded;

    /// <summary>Toggles the live prediction bar chart visibility.</summary>
    [RelayCommand] private void ToggleBarsExpanded() => IsBarsExpanded = !IsBarsExpanded;

    /// <summary>
    /// Applies the current hyperparameter selection to the block,
    /// replacing the active regressor and resetting all statistics.
    /// </summary>
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

    /// <summary>
    /// Resets the current regressor weights without changing the model type or hyperparameters.
    /// </summary>
    [RelayCommand]
    private void ResetModel()
    {
        _block.ResetModel();
        ResetStats();
        Status = "Model Reset";
    }

    /// <summary>Saves the current model state to the configured path.</summary>
    [RelayCommand]
    private void SaveModel()
    {
        try   { _block.SaveModel(); Status = "Model Saved"; }
        catch { Status = "Save Failed"; }
    }

    /// <summary>Loads a saved model state (not yet implemented).</summary>
    [RelayCommand]
    private void LoadModel() => Status = "Load not implemented";

    // ══════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// Clears all cached error history and resets observable statistics to zero.
    /// Called after model changes or resets.
    /// </summary>
    private void ResetStats()
    {
        lock (_lockObject)
        {
            _recentErrors.Clear();
            _tickCount = 0;
            _pendingCurrentError = 0;
            _pendingAverageError = 0;
            _pendingSampleCount = 0;
            _pendingConfidence = 0;
        }
        CurrentError = 0;
        AverageError = 0;
        SampleCount = 0;
        Confidence = 0;
    }

    /// <summary>
    /// Unsubscribes from block events and disposes the visualization bundle.
    /// </summary>
    public void Dispose()
    {
        _block.OnPrediction -= HandlePrediction;
        _block.OnTrainingStateChanged -= HandleTrainingStateChanged;
        _block.OnLabelsUpdated -= HandleLabelsUpdated;
        Viz.Dispose();
    }
}

/// <summary>
/// Represents a single horizontal bar in the live prediction bar chart.
/// Each item tracks one output dimension with a label, value, and color.
/// </summary>
public partial class PredictionBarItem : ObservableObject
{
    /// <summary>Display label (e.g. "Index", "WrFlex").</summary>
    public string Label { get; }

    /// <summary>Hex color string matching the corresponding scope trace (e.g. "#FF6384").</summary>
    public string ColorHex { get; }

    /// <summary>Current prediction value for this output dimension, in [0..1].</summary>
    [ObservableProperty] private double _value;

    /// <summary>
    /// Initializes a new <see cref="PredictionBarItem"/>.
    /// </summary>
    /// <param name="label">Display label for this output channel.</param>
    /// <param name="value">Initial prediction value.</param>
    /// <param name="colorHex">Hex color matching the scope trace.</param>
    public PredictionBarItem(string label, double value, string colorHex)
    {
        Label = label;
        _value = value;
        ColorHex = colorHex;
    }
}