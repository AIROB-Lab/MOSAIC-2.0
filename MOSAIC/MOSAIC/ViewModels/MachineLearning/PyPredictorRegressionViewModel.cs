using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.MachineLearning;
using MOSAIC.Visualization;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.ViewModels.MachineLearning;

public sealed partial class PyPredictorRegressionViewModel : ObservableObject, IDisposable
{
    #region Constants
    private const int UiUpdateIntervalMs = 16;
    #endregion

    #region Fields
    private readonly PyPredictorRegression _block;
    private readonly object                _lock = new();
    private Vector?  _pendingPrediction;
    private DateTime _lastUiUpdate  = DateTime.MinValue;
    private bool     _uiUpdatePending;
    #endregion

    #region Observable Properties

    public PyPredictorRegression Block => _block;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrainCommand))]
    [NotifyCanExecuteChangedFor(nameof(FineTuneCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetToCheckpointCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSmootherCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty] private int    _bufferSegmentCount;

    public ObservableCollection<OutputValue> OutputValues { get; } = new();

    [ObservableProperty] private string _savePath = string.Empty;

    [ObservableProperty] private bool _isOutputExpanded   = true;
    [ObservableProperty] private bool _isTrainingExpanded = false;

    // ── Train hyperparameters ────────────────────────────────────
    [ObservableProperty] private int    _trainEpochs = 100;
    [ObservableProperty] private int    _trainHop    = 100;
    [ObservableProperty] private int    _batchSize   = 32;
    [ObservableProperty] private double _lr          = 1e-3;

    // ── Anti-forgetting controls ─────────────────────────────────
    [ObservableProperty] private bool   _useDistillation = true;
    [ObservableProperty] private double _replayRatio     = 0.7;
    [ObservableProperty] private bool   _jointFinetune   = true;
    [ObservableProperty] private int    _jointEpochs     = 30;

    // ── Fine-tune hyperparameters ────────────────────────────────
    [ObservableProperty] private int    _ftEpochs = 50;
    [ObservableProperty] private double _ftLr     = 5e-3;

    #endregion

    #region Constructor

    public PyPredictorRegressionViewModel(PyPredictorRegression block)
    {
        _block    = block ?? throw new ArgumentNullException(nameof(block));
        _savePath = block.SavePath;

        OutputValues.Add(new OutputValue { Label = "Index",      Value = 0, IsBipolar = false });
        OutputValues.Add(new OutputValue { Label = "Middle",     Value = 0, IsBipolar = false });
        OutputValues.Add(new OutputValue { Label = "Ring",       Value = 0, IsBipolar = false });
        OutputValues.Add(new OutputValue { Label = "Little",     Value = 0, IsBipolar = false });
        OutputValues.Add(new OutputValue { Label = "Thumb",      Value = 0, IsBipolar = false });
        OutputValues.Add(new OutputValue { Label = "Wrist F/E",  Value = 0, IsBipolar = true  });
        OutputValues.Add(new OutputValue { Label = "Wrist P/S",  Value = 0, IsBipolar = true  });
        OutputValues.Add(new OutputValue { Label = "Wrist U/R",  Value = 0, IsBipolar = true  });

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

            for (int i = 0; i < OutputValues.Count && i < p.Count; i++)
                OutputValues[i].Value = p[i];

            _uiUpdatePending = false;
        }, DispatcherPriority.Background);
    }

    #endregion

    #region Commands

    private bool CanTrain()    => !IsBusy && (_block.Buffer?.dB.Count ?? 0) > 0;
    private bool CanFineTune() => !IsBusy && (_block.Buffer?.dB.Count ?? 0) > 0 && _block.IsTrained;
    private bool CanReset()    => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanTrain))]
    private async Task TrainAsync()
    {
        var buffer = _block.Buffer;
        if (buffer is null || buffer.dB.Count == 0) { Status = "Buffer is empty."; return; }

        var clusters = buffer.dB;
        await Task.Run(() => _block.TrainIncremental(
            clusters,
            epochs:          TrainEpochs,
            hop:             TrainHop,
            batchSize:       BatchSize,
            lr:              Lr,
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

        var clusters = buffer.dB;
        await Task.Run(() => _block.FineTune(
            clusters,
            epochs:   FtEpochs,
            lr:       FtLr,
            savePath: SavePath
        ));
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void ResetModel()
    {
        _block.ResetModel();
        Status = "Model reset (untrained)";
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void ResetToCheckpoint()
    {
        _block.ResetToCheckpoint();
        Status = "Reset to checkpoint";
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void ResetSmoother()
    {
        _block.ResetSmoother();
        Status = "Smoother reset";
    }

    [RelayCommand]
    private void ToggleOutputExpanded() => IsOutputExpanded = !IsOutputExpanded;

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

public sealed class OutputValue : ObservableObject
{
    private string _label = string.Empty;
    private double _value;
    private bool   _isBipolar;

    public string Label     { get => _label;     set => SetProperty(ref _label, value); }
    public double Value     { get => _value;     set => SetProperty(ref _value, value); }
    public bool   IsBipolar { get => _isBipolar; set => SetProperty(ref _isBipolar, value); }
}