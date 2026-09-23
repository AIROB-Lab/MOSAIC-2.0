using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Components.Manager.ControlAlgorithm;
using ControlAlgorithm = MOSAIC.Models.FlowControl.ControlAlgorithm;

namespace MOSAIC.ViewModels;

/// <summary>
/// ViewModel for ControlAlgorithm block.
/// </summary>
public partial class ControlAlgorithmViewModel : ObservableObject
{
    private readonly ControlAlgorithm _model;

    public IReadOnlyList<string> AvailableAlgorithms => ControlAlgorithm.AvailableAlgorithms;

    public string SelectedAlgorithm
    {
        get => _model.AlgorithmName;
        set
        {
            if (_model.AlgorithmName != value && !string.IsNullOrEmpty(value))
            {
                var strategy = ControlAlgorithm.CreateStrategy(value);
                _model.SetStrategy(strategy);
                OnPropertyChanged();
                OnPropertyChanged(nameof(AlgorithmDescription));
                OnPropertyChanged(nameof(IsDLControl));
                OnPropertyChanged(nameof(FingerDeadband));
                OnPropertyChanged(nameof(WristDeadband));
            }
        }
    }

    public string AlgorithmDescription => SelectedAlgorithm switch
    {
        "DirectControl"        => "Direct mapping: prediction [0,1] → actuation [0,100]",
        "StepwiseControl"      => "Incremental steps with 0.3 deadband threshold",
        "BidirectionalControl" => "Bidirectional with neutral rest position",
        "DLControl"            => "DL regression: 5 fingers + 3 wrist DOFs",
        _ => $"Custom: {SelectedAlgorithm}"
    };

    /// <summary>
    /// True when the active strategy is DLControlStrategy.
    /// Used by the CardView to show/hide DL-specific controls.
    /// </summary>
    public bool IsDLControl => _model.Strategy is DLControlStrategy;

    /// <summary>
    /// Finger deadband [0..1]. Values below this threshold produce 0 actuation.
    /// Only meaningful when DLControl is active.
    /// </summary>
    public double FingerDeadband
    {
        get => _model.Strategy is DLControlStrategy dl ? dl.FingerDeadband : 0;
        set
        {
            if (_model.Strategy is DLControlStrategy dl && dl.FingerDeadband != value)
            {
                dl.FingerDeadband = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Wrist deadband [0..1]. Absolute bipolar values below this snap to neutral.
    /// Only meaningful when DLControl is active.
    /// </summary>
    public double WristDeadband
    {
        get => _model.Strategy is DLControlStrategy dl ? dl.WristDeadband : 0;
        set
        {
            if (_model.Strategy is DLControlStrategy dl && dl.WristDeadband != value)
            {
                dl.WristDeadband = value;
                OnPropertyChanged();
            }
        }
    }

    public string FrequencyText => _model.FrequencyText;

    public ControlAlgorithmViewModel(ControlAlgorithm model)
    {
        _model = model;

        _model.PropertyChanged += (s, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ControlAlgorithm.FrequencyText):
                    OnPropertyChanged(nameof(FrequencyText));
                    break;
                case nameof(ControlAlgorithm.AlgorithmName):
                    OnPropertyChanged(nameof(SelectedAlgorithm));
                    OnPropertyChanged(nameof(AlgorithmDescription));
                    OnPropertyChanged(nameof(IsDLControl));
                    OnPropertyChanged(nameof(FingerDeadband));
                    OnPropertyChanged(nameof(WristDeadband));
                    break;
            }
        };
    }

    [RelayCommand]
    private void Reset() => _model.ResetStrategy();
}