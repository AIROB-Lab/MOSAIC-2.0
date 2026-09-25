using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.MachineLearning.Factory;
using MOSAIC.Models.Learning;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.ViewModels.Learning;

/// <summary>
/// Static converters for HybridPredictorView.
/// </summary>
public static class HybridConverters
{
    public static readonly IValueConverter BoolToTrainingColor = new FuncValueConverter<bool, Color>(
        isTraining => isTraining ? Color.Parse("#00AA44") : Color.Parse("#444444"));

    public static readonly IValueConverter BoolToSelectedColor = new FuncValueConverter<bool, IBrush>(
        isSelected => isSelected ? new SolidColorBrush(Color.Parse("#9933FF")) : new SolidColorBrush(Color.Parse("#B0B0B0")));

    public static readonly IValueConverter BoolToBarColor = new FuncValueConverter<bool, IBrush>(
        isSelected => isSelected ? new SolidColorBrush(Color.Parse("#9933FF")) : new SolidColorBrush(Color.Parse("#666666")));
}


/// <summary>
/// ViewModel for HybridPredictorBlock UI.
/// </summary>
public partial class HybridPredictorViewModel : ObservableObject, IDisposable
{
    private readonly HybridPredictorBlock _block;
    private readonly object _lockObject = new();
    private bool _disposed;

    // === Observable Fields ===

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedModelDescription))]
    [NotifyPropertyChangedFor(nameof(IsRffModel))]
    private ClassifierType _selectedModelType;

    [ObservableProperty]
    private double _learningRate;

    [ObservableProperty]
    private double _lambda;

    [ObservableProperty]
    private double _sigma;

    [ObservableProperty]
    private int _featureDim;

    [ObservableProperty]
    private bool _isTraining;

    [ObservableProperty]
    private string _status = "Waiting for classes...";

    [ObservableProperty]
    private double _confidence;

    [ObservableProperty]
    private int _sampleCount;

    [ObservableProperty]
    private string _currentClassName = "None";

    [ObservableProperty]
    private string _predictedClassName = "Unknown";

    [ObservableProperty]
    private int _numClasses;

    [ObservableProperty]
    private double[] _classProbabilities = Array.Empty<double>();

    // === Computed Properties ===

    public HybridPredictorBlock Block => _block;
    public string Name => _block.Name;
    public int InputDimension => _block.InputDim;
    public int OutputDimension => _block.OutputDim;
    public string FrequencyText => $"{_block.DesiredRate:F0} Hz";

    public List<ClassifierType> AvailableModelTypes { get; } = new(Enum.GetValues<ClassifierType>());

    public string SelectedModelDescription => ClassifierFactory.GetDescription(SelectedModelType);
    public bool IsRffModel => SelectedModelType == ClassifierType.SoftmaxRFF;
    public string TrainingButtonText => IsTraining ? "Stop Training" : "Start Training";
    public double ConfidencePercent => Confidence * 100;
    public string SampleCountText => $"{SampleCount:N0} samples";

    /// <summary>
    /// Learned classes with their probabilities.
    /// </summary>
    public ObservableCollection<ClassProbabilityItem> ClassItems { get; } = new();

    public HybridPredictorViewModel(HybridPredictorBlock block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));

        // Initialize from block's current state
        _selectedModelType = _block.ModelType;
        _learningRate = _block.Config.LearningRate;
        _lambda = _block.Config.Lambda;
        _sigma = _block.Config.Sigma;
        _featureDim = _block.Config.FeatureDim;
        _isTraining = _block.IsTraining;
        _status = _block.TrainingMessage;
        _confidence = _block.Confidence;
        _sampleCount = _block.SampleCount;
        _currentClassName = _block.CurrentClassName;
        _predictedClassName = _block.PredictedClassName;
        _numClasses = _block.NumClasses;
        _classProbabilities = _block.ClassProbabilities;

        // Subscribe to block events
        _block.OnPrediction += HandlePrediction;
        _block.OnTrainingStateChanged += HandleTrainingStateChanged;
        _block.OnClassLearned += HandleClassLearned;

        // Initialize class items
        UpdateClassItems();
    }

    private void HandlePrediction(Vector<double> output, int predictedClass, double[] probabilities, double confidence, int sampleCount)
    {
        if (_disposed) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;

            try
            {
                lock (_lockObject)
                {
                    PredictedClassName = predictedClass >= 0 && predictedClass < _block.ClassNames.Count
                        ? _block.ClassNames[predictedClass]
                        : "Unknown";
                    ClassProbabilities = probabilities;
                    Confidence = confidence;
                    SampleCount = sampleCount;

                    // Update class items with new probabilities
                    for (int i = 0; i < ClassItems.Count && i < probabilities.Length; i++)
                    {
                        ClassItems[i].Probability = probabilities[i];
                        ClassItems[i].IsSelected = (i == predictedClass);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Name}] UI update error: {ex.Message}");
            }
        });
    }

    private void HandleTrainingStateChanged(bool isTraining, string? className)
    {
        if (_disposed) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;

            try
            {
                IsTraining = isTraining;
                CurrentClassName = className ?? "None";
                Status = _block.TrainingMessage;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Name}] Training state error: {ex.Message}");
            }
        });
    }

    private void HandleClassLearned(string name, Vector<double> vector)
    {
        if (_disposed) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;

            try
            {
                NumClasses = _block.NumClasses;
                Status = _block.TrainingMessage;
                UpdateClassItems();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Name}] Class learned error: {ex.Message}");
            }
        });
    }

    private void UpdateClassItems()
    {
        ClassItems.Clear();
        for (int i = 0; i < _block.ClassNames.Count; i++)
        {
            var vector = _block.GetClassVector(i);
            ClassItems.Add(new ClassProbabilityItem
            {
                Index = i,
                Name = _block.ClassNames[i],
                Probability = i < ClassProbabilities.Length ? ClassProbabilities[i] : 0,
                IsSelected = false,
                TargetVector = vector != null ? string.Join(", ", vector.Select(v => v.ToString("F1"))) : ""
            });
        }
    }

    // === Commands ===

    [RelayCommand]
    private void ApplyModel()
    {
        var config = new ClassifierConfig
        {
            LearningRate = LearningRate,
            Lambda = Lambda,
            Sigma = Sigma,
            FeatureDim = FeatureDim
        };

        _block.ChangeModel(SelectedModelType, config);

        lock (_lockObject)
        {
            SampleCount = 0;
            Confidence = 0;
        }

        Status = _block.TrainingMessage;
    }

    [RelayCommand]
    private void ResetModel()
    {
        _block.ResetModel();

        lock (_lockObject)
        {
            SampleCount = 0;
            Confidence = 0;
        }

        Status = "Model Reset";
    }

    [RelayCommand]
    private void ClearClasses()
    {
        _block.ClearClasses();
        ClassItems.Clear();
        NumClasses = 0;
        SampleCount = 0;
        Confidence = 0;
        Status = "Waiting for classes...";
    }

    [RelayCommand]
    private void SaveModel()
    {
        try
        {
            _block.SaveModel();
            Status = "Model Saved";
        }
        catch (Exception ex)
        {
            Status = "Save Failed";
            Console.WriteLine($"Save failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _block.OnPrediction -= HandlePrediction;
        _block.OnTrainingStateChanged -= HandleTrainingStateChanged;
        _block.OnClassLearned -= HandleClassLearned;
    }
}

/// <summary>
/// Extended class probability item with target vector info.
/// </summary>
public partial class ClassProbabilityItem : ObservableObject
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    
    [ObservableProperty]
    private double _probability;
    
    [ObservableProperty]
    private bool _isSelected;
    
    public string TargetVector { get; init; } = "";
    
    public double ProbabilityPercent => Probability * 100;
}
