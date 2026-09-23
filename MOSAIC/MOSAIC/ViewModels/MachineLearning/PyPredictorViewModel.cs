using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.MachineLearning;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.ViewModels.MachineLearning;

public sealed partial class PyPredictorViewModel : ObservableObject, IDisposable
{
    #region Constants
    private const int UiUpdateIntervalMs = 16;
    #endregion

    #region Fields
    private readonly PyPredictor _block;
    private readonly object      _lock = new();
    private Vector?  _pendingPrediction;
    private DateTime _lastUiUpdate  = DateTime.MinValue;
    private bool     _uiUpdatePending;
    #endregion

    #region Observable Properties

    public PyPredictor Block => _block;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrainCommand))]
    [NotifyCanExecuteChangedFor(nameof(FineTuneCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status        = "Idle";
    [ObservableProperty] private int    _predictedClass = -1;
    [ObservableProperty] private double _topConfidence;
    [ObservableProperty] private int    _bufferSegmentCount;

    public ObservableCollection<ClassProbability> ClassProbabilities { get; } = new();

    [ObservableProperty] private string _savePath = string.Empty;

    [ObservableProperty] private bool _isBarsExpanded     = true;
    [ObservableProperty] private bool _isTrainingExpanded = false;

    [ObservableProperty] private int    _trainEpochs     = 30;
    [ObservableProperty] private bool   _useDistillation = true;
    [ObservableProperty] private double _replayRatio     = 0.5;
    [ObservableProperty] private bool   _jointFinetune   = true;
    [ObservableProperty] private int    _jointEpochs     = 20;

    [ObservableProperty] private int    _ftEpochs           = 30;
    [ObservableProperty] private int    _ftHop              = 100;
    [ObservableProperty] private int    _ftBatchSize        = 64;
    [ObservableProperty] private double _ftLearningRate     = 3e-4;
    [ObservableProperty] private int    _ftOversampleFactor = 3;

    #endregion

    #region Constructor

    public PyPredictorViewModel(PyPredictor block)
    {
        _block    = block ?? throw new ArgumentNullException(nameof(block));
        _savePath = block.SavePath;

        for (int i = 0; i < block.NumClasses; i++)
            ClassProbabilities.Add(new ClassProbability { Label = $"Class {i}", Value = 0 });

        _block.OnPrediction           += HandlePrediction;
        _block.OnTrainingStateChanged += HandleTrainingState;
        _block.OnBufferSegmentAdded   += HandleBufferSegmentAdded;
    }

    #endregion

    #region Event Handlers

    private void HandleBufferSegmentAdded()
    {
        Dispatcher.UIThread.Post(() =>
        {
            BufferSegmentCount = _block.Buffer?.dB.Count ?? 0;
            TrainCommand.NotifyCanExecuteChanged();
            FineTuneCommand.NotifyCanExecuteChanged();
        });
    }

    private void HandleTrainingState(bool isTraining)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsBusy = isTraining;
            Status = isTraining ? "Training…" : "Idle";
            FineTuneCommand.NotifyCanExecuteChanged();
        });
    }

    private void HandlePrediction(Vector probs)
    {
        lock (_lock) { _pendingPrediction = probs; }

        var now = DateTime.UtcNow;
        if ((now - _lastUiUpdate).TotalMilliseconds < UiUpdateIntervalMs || _uiUpdatePending)
            return;

        _uiUpdatePending = true;
        _lastUiUpdate    = now;

        Dispatcher.UIThread.Post(() =>
        {
            Vector? p;
            lock (_lock) { p = _pendingPrediction; }
            if (p is null) { _uiUpdatePending = false; return; }

            int maxIdx = 0;
            for (int i = 0; i < ClassProbabilities.Count && i < p.Count; i++)
            {
                ClassProbabilities[i].Value = p[i];
                if (p[i] > p[maxIdx]) maxIdx = i;
            }

            PredictedClass   = maxIdx;
            TopConfidence    = p[maxIdx];
            _uiUpdatePending = false;
        }, DispatcherPriority.Background);
    }

    #endregion

    #region Commands

    private bool CanTrain()    => !IsBusy && (_block.Buffer?.dB.Count ?? 0) > 0;
    private bool CanFineTune() => !IsBusy && (_block.Buffer?.dB.Count ?? 0) > 0 && _block.IsTrained;

    /// <summary>
    /// Run incremental training on every segment in the buffer. With the buffer's new
    /// 3-tuple shape <c>(Label, Targets, Data)</c>, <see cref="PyPredictor.TrainIncremental"/>
    /// takes it directly — no projection helper needed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTrain))]
    private async Task TrainAsync()
    {
        var buffer = _block.Buffer;
        if (buffer is null || buffer.dB.Count == 0) { Status = "Buffer is empty."; return; }

        await Task.Run(() => _block.TrainIncremental(
            buffer.dB,
            epochs:          TrainEpochs,
            useDistillation: UseDistillation,
            replayRatio:     ReplayRatio,
            jointFinetune:   JointFinetune,
            jointEpochs:     JointEpochs,
            savePath:        SavePath
        ));
    }

    [RelayCommand(CanExecute = nameof(CanFineTune))]
    private async Task FineTuneAsync()
    {
        var buffer = _block.Buffer;
        if (buffer is null || buffer.dB.Count == 0) { Status = "Buffer is empty."; return; }

        await Task.Run(() => _block.FineTune(
            buffer.dB,
            epochs:           FtEpochs,
            hop:              FtHop,
            batchSize:        FtBatchSize,
            lr:               FtLearningRate,
            oversampleFactor: FtOversampleFactor
        ));
    }

    [RelayCommand]
    private void ToggleBarsExpanded() => IsBarsExpanded = !IsBarsExpanded;

    [RelayCommand]
    private void ToggleTrainingExpanded()
    {
        IsTrainingExpanded = !IsTrainingExpanded;
        if (IsTrainingExpanded)
        {
            BufferSegmentCount = _block.Buffer?.dB.Count ?? 0;
            TrainCommand.NotifyCanExecuteChanged();
            FineTuneCommand.NotifyCanExecuteChanged();
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        _block.OnPrediction           -= HandlePrediction;
        _block.OnTrainingStateChanged -= HandleTrainingState;
        _block.OnBufferSegmentAdded   -= HandleBufferSegmentAdded;
    }

    #endregion
}

public sealed class ClassProbability : ObservableObject
{
    private string _label = string.Empty;
    private double _value;
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public double Value { get => _value; set => SetProperty(ref _value, value); }
}