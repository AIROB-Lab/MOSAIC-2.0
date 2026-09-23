using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MathNet.Numerics.LinearAlgebra;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Components.MachineLearning.Interfaces;
using MOSAIC.Components.MachineLearning.Factory;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;

namespace MOSAIC.Models.Learning;

/// <summary>
/// Hybrid Predictor Block - combines classifier training with continuous vector output.
/// 
/// Training: Like a classifier - learns from discrete class labels (action names from Trigger)
/// Output: Continuous vector - weighted sum of class target vectors by predicted probabilities
/// 
/// Example:
///   Classes defined in Trigger:
///     rest:  [0,0,0,0,0,0]
///     power: [1,1,1,1,0,0]  
///     pinch: [0,0,0,0,1,1]
///   
///   Predicted probabilities: [0.1, 0.7, 0.2]
///   
///   Output = 0.1*rest + 0.7*power + 0.2*pinch
///          = [0.7, 0.7, 0.7, 0.7, 0.2, 0.2]
/// 
/// This gives smooth, continuous control signals for prosthesis control while
/// training with simple discrete gesture labels.
/// 
/// Inputs:
/// - Input[0]: Data source (feature vectors)
/// - Input[1]: Trigger (provides class labels AND target vectors)
/// 
/// JSON Config:
/// {
///   "Type": "HybridPredictor",
///   "Inputs": ["DataSource", "Trigger"],
///   "DesiredRate": 200,
///   "Params": ["SoftmaxRFF", 8, 0.01, 0.001, 1.0, 300],
///   "Path": "models/"
/// }
/// 
/// Params: [modelType, inputDim, learningRate?, lambda?, sigma?, featureDim?]
/// Note: Classes are automatically learned from Trigger actions!
/// </summary>
/// <example>
/// <para>Block entry for a larger pipeline. Params: classifier model, feature dimension, learning rate, regularization, RFF kernel width, RFF feature count. Classes are discovered from Trigger action names; there is no numClasses parameter. Define an 8-value feature stream and a Trigger supplying targets.</para>
/// <code language="json">
/// {
///   "HybridPredictor": {
///     "Type": "hybridpredictor",
///     "Inputs": ["Features", "Capture"],
///     "Params": ["Softmax", 8, 0.01, 0.001, 1.0, 300]
///   }
/// }
/// </code>
/// </example>
public sealed partial class HybridPredictorBlock : BaseBlock
{
    /// <inheritdoc />
    public override int MinInputs => 1;

    /// <inheritdoc />
    public override int MaxInputs => 2;

    private IClassifier _model;
    private int _currentLabel = -1;
    private Vector<double>? _lastOutput;
    private readonly string? _savePath;
    
    // Class name -> (index, target vector)
    private readonly Dictionary<string, int> _classIndices = new();
    private readonly List<string> _classNames = new();
    private readonly List<Vector<double>> _classVectors = new();
    private bool _isInitialized = false;

    // === Configuration ===
    public int InputDim { get; }
    public int OutputDim => _classVectors.Count > 0 ? _classVectors[0].Count : 0;
    public int NumClasses => _classNames.Count;
    public IReadOnlyList<string> ClassNames => _classNames;
    public ClassifierType ModelType { get; private set; }
    public ClassifierConfig Config { get; private set; }

    // === Observable State ===
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrainingStatusText))]
    private bool _isTraining;

    [ObservableProperty]
    private double _confidence;

    [ObservableProperty]
    private int _sampleCount;

    [ObservableProperty]
    private string _currentClassName = "None";

    [ObservableProperty]
    private string _predictedClassName = "Unknown";

    [ObservableProperty]
    private double[] _classProbabilities = Array.Empty<double>();

    [ObservableProperty]
    private string _trainingMessage = "Waiting for classes...";

    public string TrainingStatusText => IsTraining ? $"Training: {CurrentClassName}" : "Predicting";

    /// <summary>
    /// Event fired when a new prediction is made.
    /// Args: (outputVector, predictedClass, probabilities, confidence, sampleCount)
    /// </summary>
    public event Action<Vector<double>, int, double[], double, int>? OnPrediction;

    /// <summary>
    /// Event fired when training state changes.
    /// </summary>
    public event Action<bool, string?>? OnTrainingStateChanged;

    /// <summary>
    /// Event fired when a new class is learned.
    /// </summary>
    public event Action<string, Vector<double>>? OnClassLearned;

    public HybridPredictorBlock(
        string name,
        double desiredRate,
        int inputDim,
        ClassifierType modelType = ClassifierType.Softmax,
        ClassifierConfig? config = null,
        string? savePath = null)
    {
        Name = name;
        DesiredRate = desiredRate;
        InputDim = inputDim;
        ModelType = modelType;
        Config = config ?? new ClassifierConfig();
        _savePath = savePath;

        // Model will be created when we know the number of classes
        _model = null!;

        Console.WriteLine($"[{Name}] Initialized: {ModelType}, InputDim={InputDim}, waiting for classes from Trigger...");
    }

    /// <summary>
    /// Creates block from JSON configuration.
    /// </summary>
    public static HybridPredictorBlock ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "HybridPredictor";
        var rate = m.DesiredRate ?? 200;

        if (m.Params is null || m.Params.Count < 2)
            throw new ArgumentException($"HybridPredictor '{name}' requires params: [modelType, inputDim, learningRate?, lambda?, sigma?, featureDim?]");

        var modelTypeStr = GetString(m.Params[0], "Softmax");
        if (!Enum.TryParse<ClassifierType>(modelTypeStr, ignoreCase: true, out var modelType))
            throw new ArgumentException($"Unknown classifier type '{modelTypeStr}'. Available: {string.Join(", ", Enum.GetNames<ClassifierType>())}");

        var inputDim = GetInt(m.Params[1]);

        var config = new ClassifierConfig
        {
            LearningRate = m.Params.Count > 2 ? GetDouble(m.Params[2], 0.01) : 0.01,
            Lambda = m.Params.Count > 3 ? GetDouble(m.Params[3], 0.001) : 0.001,
            Sigma = m.Params.Count > 4 ? GetDouble(m.Params[4], 1.0) : 1.0,
            FeatureDim = m.Params.Count > 5 ? GetInt(m.Params[5], 300) : 300
        };

        Console.WriteLine($"[{name}] Config: {modelType}, lr={config.LearningRate}, λ={config.Lambda}, σ={config.Sigma}, D={config.FeatureDim}");

        return ActivatorUtilities.CreateInstance<HybridPredictorBlock>(
            sp, name, rate, inputDim, modelType, config, m.Path);
    }

    /// <summary>
    /// <c>Path</c> is this block's model directory, not a CSV destination.
    /// </summary>
    /// <remarks>
    /// The only block in the pipeline that reads <c>Path</c> as something other than a recording
    /// folder without also recording there. Without this the factory would drop a CSV in among the
    /// saved model files.
    /// </remarks>
    protected override bool PathIsDumpFolder => false;

    /// <summary>
    /// Puts the model directory back in the config on save.
    /// </summary>
    /// <remarks>
    /// <see cref="BaseBlock.ToJsonModel"/> exports <c>GetJsonPath() ?? _explicitDumpFolder</c>, and
    /// this block opens no dumper, so without the override both halves were null and saving a
    /// pipeline silently dropped the model directory.
    /// </remarks>
    protected override string? GetJsonPath() => _savePath;

    /// <summary>
    /// The six values <c>ConfigureInput</c> reads, in its order. Taken from the live model and config
    /// rather than the constructor arguments, so a model swapped at run time is what gets saved.
    /// </summary>
    protected override IReadOnlyList<object>? GetJsonParams()
        => [ModelType.ToString(), InputDim, Config.LearningRate, Config.Lambda, Config.Sigma, Config.FeatureDim];

    /// <summary>
    /// Handles incoming data from connected blocks.
    /// </summary>
    protected override void OnReceive(object sender, object data)
    {
        try
        {
            var senderTypeName = sender?.GetType().Name ?? "";
            var isTrigger = senderTypeName == "Trigger" || senderTypeName.Contains("Trigger");

            if (isTrigger)
            {
                HandleTriggerInput(sender, data);
                return;
            }

            // From data source: process input vector
            if (data is Vector<double> x && x.Count == InputDim)
            {
                HandleDataInput(x);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{Name}] Error: {ex.Message}");
        }
    }

    private void HandleTriggerInput(object sender, object? data)
    {
        if (data is Vector<double> targetVector)
        {
            // Get action name from Trigger
            var prop = sender?.GetType().GetProperty("CurrentActionName");
            var actionName = prop?.GetValue(sender)?.ToString() ?? "";

            if (string.IsNullOrEmpty(actionName))
            {
                Console.WriteLine($"[{Name}] Warning: Empty action name from Trigger");
                return;
            }

            // Learn new class if we haven't seen it before
            if (!_classIndices.ContainsKey(actionName))
            {
                LearnNewClass(actionName, targetVector);
            }

            // Start training with this class
            _currentLabel = _classIndices[actionName];
            IsTraining = true;
            CurrentClassName = actionName;
            TrainingMessage = $"Training: {actionName}";

            OnTrainingStateChanged?.Invoke(true, CurrentClassName);
            Console.WriteLine($"[{Name}] Training STARTED: {actionName} (class {_currentLabel})");
        }
        else if (data == null)
        {
            // Stop training
            var wasTraining = IsTraining;
            _currentLabel = -1;
            IsTraining = false;
            CurrentClassName = "None";
            TrainingMessage = _isInitialized ? $"Ready ({NumClasses} classes)" : "Waiting for classes...";

            Console.WriteLine($"[{Name}] Training STOPPED (was training: {wasTraining})");

            if (wasTraining)
            {
                AutoSave();
                OnTrainingStateChanged?.Invoke(false, null);
                Console.WriteLine($"[{Name}] Samples: {SampleCount}");
            }
        }
    }

    private void LearnNewClass(string name, Vector<double> targetVector)
    {
        var index = _classNames.Count;
        _classNames.Add(name);
        _classVectors.Add(targetVector.Clone());
        _classIndices[name] = index;

        Console.WriteLine($"[{Name}] Learned new class [{index}]: '{name}' -> {targetVector}");
        OnClassLearned?.Invoke(name, targetVector);

        // Reinitialize classifier with new class count
        ReinitializeClassifier();
    }

    private void ReinitializeClassifier()
    {
        if (_classNames.Count < 2)
        {
            Console.WriteLine($"[{Name}] Need at least 2 classes to initialize classifier (have {_classNames.Count})");
            _isInitialized = false;
            return;
        }

        // Create new classifier with updated class count
        _model = ClassifierFactory.Create(ModelType, InputDim, NumClasses, Config);
        _classProbabilities = new double[NumClasses];
        _isInitialized = true;
        SampleCount = 0;  // Reset sample count since model is new

        TrainingMessage = $"Ready ({NumClasses} classes)";
        Console.WriteLine($"[{Name}] Classifier initialized with {NumClasses} classes: {string.Join(", ", _classNames)}");
    }

    private void HandleDataInput(Vector<double> x)
    {
        if (!_isInitialized)
        {
            // Can't do anything without at least 2 classes
            return;
        }

        try
        {
            // Update model if training
            if (IsTraining && _currentLabel >= 0)
            {
                _model.Update(x, _currentLabel);
                SampleCount++;
            }

            // Predict
            Confidence = _model.Confidence(x);
            var predictedClass = _model.Predict(x);
            var probs = _model.PredictProbabilities(x);

            ClassProbabilities = probs;
            PredictedClassName = predictedClass >= 0 && predictedClass < _classNames.Count
                ? _classNames[predictedClass]
                : "Unknown";

            // Compute weighted output vector
            var output = ComputeWeightedOutput(probs);
            _lastOutput = output;

            // Notify listeners
            try
            {
                OnPrediction?.Invoke(output, predictedClass, probs, Confidence, SampleCount);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Name}] Error in OnPrediction handler: {ex.Message}");
            }

            // Publish continuous output vector
            Publish(output);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{Name}] Error handling data: {ex.Message}");
        }
    }

    /// <summary>
    /// Computes weighted sum of class vectors based on probabilities.
    /// Output = sum(prob[i] * classVector[i])
    /// </summary>
    private Vector<double> ComputeWeightedOutput(double[] probabilities)
    {
        if (_classVectors.Count == 0)
            return Vector<double>.Build.Dense(1);

        var outputDim = _classVectors[0].Count;
        var output = Vector<double>.Build.Dense(outputDim, 0.0);

        for (int i = 0; i < _classVectors.Count && i < probabilities.Length; i++)
        {
            output += probabilities[i] * _classVectors[i];
        }

        return output;
    }

    // === Public Methods ===

    /// <summary>
    /// Manually add a class with its target vector.
    /// </summary>
    public void AddClass(string name, Vector<double> targetVector)
    {
        if (_classIndices.ContainsKey(name))
        {
            Console.WriteLine($"[{Name}] Class '{name}' already exists");
            return;
        }

        LearnNewClass(name, targetVector);
    }

    /// <summary>
    /// Gets the target vector for a class.
    /// </summary>
    public Vector<double>? GetClassVector(string name)
    {
        if (_classIndices.TryGetValue(name, out var index))
            return _classVectors[index].Clone();
        return null;
    }

    /// <summary>
    /// Gets the target vector for a class index.
    /// </summary>
    public Vector<double>? GetClassVector(int index)
    {
        if (index >= 0 && index < _classVectors.Count)
            return _classVectors[index].Clone();
        return null;
    }

    /// <summary>
    /// Changes the model type. Resets the model but keeps learned classes.
    /// </summary>
    public void ChangeModel(ClassifierType type, ClassifierConfig? config = null)
    {
        ModelType = type;
        Config = config ?? Config;
        
        if (_isInitialized)
        {
            _model = ClassifierFactory.Create(ModelType, InputDim, NumClasses, Config);
            SampleCount = 0;
            Confidence = 0;
            _classProbabilities = new double[NumClasses];
        }

        Console.WriteLine($"[{Name}] Model changed to {type}");
    }

    /// <summary>
    /// Resets the model but keeps learned classes.
    /// </summary>
    public void ResetModel()
    {
        if (_isInitialized)
        {
            _model.Reset();
            SampleCount = 0;
            Confidence = 0;
            _classProbabilities = new double[NumClasses];
        }

        Console.WriteLine($"[{Name}] Model reset");
    }

    /// <summary>
    /// Clears all learned classes and resets everything.
    /// </summary>
    public void ClearClasses()
    {
        _classNames.Clear();
        _classVectors.Clear();
        _classIndices.Clear();
        _isInitialized = false;
        SampleCount = 0;
        Confidence = 0;
        _classProbabilities = Array.Empty<double>();
        TrainingMessage = "Waiting for classes...";

        Console.WriteLine($"[{Name}] All classes cleared");
    }

    /// <summary>
    /// Gets the last output vector.
    /// </summary>
    public Vector<double>? LastOutput => _lastOutput;

    // === State Persistence ===

    public void SaveModel(string? path = null)
    {
        path ??= GetDefaultSavePath();
        if (path is null) return;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Save class definitions and model state
        var state = new HybridPredictorState
        {
            ClassNames = _classNames.ToArray(),
            ClassVectors = _classVectors.Select(v => v.ToArray()).ToArray(),
            SampleCount = SampleCount
        };

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);

        Console.WriteLine($"[{Name}] Saved to {path} ({NumClasses} classes, {SampleCount} samples)");
    }

    public void LoadModel(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Model file not found: {path}");

        var json = File.ReadAllText(path);
        var state = JsonSerializer.Deserialize<HybridPredictorState>(json);

        if (state?.ClassNames != null && state.ClassVectors != null)
        {
            ClearClasses();

            for (int i = 0; i < state.ClassNames.Length && i < state.ClassVectors.Length; i++)
            {
                var name = state.ClassNames[i];
                var vector = Vector<double>.Build.Dense(state.ClassVectors[i]);
                LearnNewClass(name, vector);
            }

            Console.WriteLine($"[{Name}] Loaded from {path} ({NumClasses} classes)");
        }
    }

    public void AutoSave()
    {
        if (SampleCount == 0 || !_isInitialized)
        {
            Console.WriteLine($"[{Name}] Skipping auto-save (no data)");
            return;
        }

        var path = GetDefaultSavePath();
        if (path is not null)
        {
            SaveModel(path);
        }
    }

    private string? GetDefaultSavePath()
    {
        if (_savePath is null) return null;

        Directory.CreateDirectory(_savePath);
        var filename = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Name}_{NumClasses}classes_{SampleCount}samples.json";
        return Path.Combine(_savePath, filename);
    }

    public override void Dispose()
    {
        if (SampleCount > 0 && _isInitialized)
        {
            AutoSave();
        }
        base.Dispose();
    }

    #region JSON Parsing Helpers

    private static string GetString(object? obj, string defaultValue = "") => obj switch
    {
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? defaultValue,
        JsonElement je => je.GetRawText().Trim('"'),
        string s => s,
        _ => obj?.ToString() ?? defaultValue
    };

    private static int GetInt(object? obj, int defaultValue = 0) => obj switch
    {
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.TryGetInt32(out var i) ? i : (int)je.GetDouble(),
        JsonElement je when je.ValueKind == JsonValueKind.String => int.TryParse(je.GetString(), out var i) ? i : defaultValue,
        int i => i,
        double d => (int)d,
        _ => defaultValue
    };

    private static double GetDouble(object? obj, double defaultValue = 0.0) => obj switch
    {
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble(),
        JsonElement je when je.ValueKind == JsonValueKind.String => double.TryParse(je.GetString(), out var d) ? d : defaultValue,
        double d => d,
        _ => defaultValue
    };

    #endregion
}

/// <summary>
/// State for serializing HybridPredictor.
/// </summary>
internal record HybridPredictorState
{
    public string[] ClassNames { get; init; } = Array.Empty<string>();
    public double[][] ClassVectors { get; init; } = Array.Empty<double[]>();
    public int SampleCount { get; init; }
}
