using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Manager.Python;
using MOSAIC.Models.FlowControl;
using MOSAIC.Visualization;
using Python.Runtime;
using static MOSAIC.Components.Basics.JsonModel;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Models.MachineLearning;

/// <summary>
/// Online ultrasound gesture classifier — ResNet-18 (single-frame B-mode in,
/// softmax probabilities out) driven through the Python.NET bridge.
/// </summary>
/// <remarks>
/// <para>
/// Designed to mirror <see cref="PyPredictorRegression"/> so the two predictor
/// families share UI / wiring patterns. Each incoming <see cref="Matrix"/> from
/// an upstream ultrasound source is treated as one B-mode frame; the block
/// pushes it through the Python <c>Model</c>, receives a softmax probability
/// vector of length <see cref="NumClasses"/>, and publishes it downstream.
/// </para>
/// <para>
/// Training data is accumulated by an upstream <see cref="TriggerBuffer"/>:
/// segments arrive as <c>(Vector Target, Matrix Data)</c> tuples where the
/// target's first element is the class index and the matrix's rows are
/// successive frame samples for that label. The ViewModel calls
/// <see cref="TrainIncremental"/> to dispatch them through the Python model's
/// <c>train_incremental</c> entry point.
/// </para>
/// <para>
/// <b>JSON configuration:</b>
/// <code>
/// "UsClassifier": {
///   "Type":   "UltrasoundClassifier",
///   "Inputs": ["Ultrasound", "Buffer"],
///   "Params": ["your_classifier_module", 9, 224, 224, 1e-4, 42, ""]
/// }
/// </code>
/// Params: <c>[ modulePath, numClasses, inH, inW, learningRate, randomSeed, modelPath ]</c>.
/// The public repository does not include pretrained weights; <c>modelPath</c> must point to a
/// user-generated or independently licensed artifact.
/// </para>
/// </remarks>
public sealed partial class UltrasoundClassifier : BaseBlock
{
    /// <summary>Needs exactly two inputs (e.g. a data stream + a Trigger/TriggerBuffer).</summary>
    public override int MinInputs => 2;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 2;

    #region Fields

    private dynamic? _np;
    private dynamic? _model;
    private DateTime _lastVizUpdate     = DateTime.MinValue;
    private const int VizUpdateIntervalMs = 16; // ~60 fps cap on viz updates
    private int _lastFrameH; // remembered for training-time un-stacking

    #endregion

    #region Observable Properties

    [ObservableProperty] private string _modulePath   = "us_classifier";
    [ObservableProperty] private int    _numClasses   = 9;
    [ObservableProperty] private int    _inH          = 224;
    [ObservableProperty] private int    _inW          = 224;
    [ObservableProperty] private double _learningRate = 1e-4;
    [ObservableProperty] private int    _randomSeed   = 42;
    [ObservableProperty] private bool   _isRegression;
    [ObservableProperty] private bool   _isTraining;
    [ObservableProperty] private bool   _isTrained;
    [ObservableProperty] private long   _predictionCount;
    [ObservableProperty] private int    _predictedClass;
    [ObservableProperty] private double _predictedConfidence;
    [ObservableProperty] private string _modelDescription = string.Empty;

    /// <summary>
    /// Path where the model's state_dict is saved / loaded. Initially populated
    /// from the JSON config's <c>modelPath</c> param; the UI's TextBox binds to
    /// this so the user can edit it and click Save/Load. Empty = no file
    /// associated yet.
    /// </summary>
    [ObservableProperty] private string _modelPath = string.Empty;

    #endregion

    #region Public surface

    /// <summary>
    /// Visualization bundle for live prediction output. Settable so the
    /// ViewModel can attach its own instance (mirrors <c>IncrementalPredictor</c>'s
    /// pattern, where the VM owns the visualization and the block feeds it).
    /// </summary>
    private BlockVisualization _viz = new();

    /// <summary>Live plot for this block. Assigned by the ViewModel, which owns the scope.</summary>
    /// <remarks>
    /// Setting this re-pushes the publish rate: the rate is normally pushed when it changes,
    /// which for these blocks happens during construction — before the ViewModel has handed
    /// over the scope — so without this the scope would never learn its time base.
    /// </remarks>
    public BlockVisualization Viz
    {
        get => _viz;
        set { _viz = value; RefreshVisualizationRate(); }
    }

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    public event Action<Vector>? OnPrediction;
    public event Action<bool>?   OnTrainingStateChanged;
    public event Action?         OnBufferSegmentAdded;

    public TriggerBuffer? Buffer { get; set; }

    #endregion

    #region Constructor & Factory

    public UltrasoundClassifier(
        string name,
        double desiredRate,
        string modulePath,
        int    numClasses    = 9,
        int    inH           = 224,
        int    inW           = 224,
        double learningRate  = 1e-4,
        int    randomSeed    = 42,
        string modelPath     = "",
        bool   isRegression  = false)
        : base(name, desiredRate)
    {
        _modulePath    = modulePath;
        _numClasses    = numClasses;
        _inH           = inH;
        _inW           = inW;
        _learningRate  = learningRate;
        _randomSeed    = randomSeed;
        _modelPath     = modelPath;
        _isRegression  = isRegression;

        UpdateDescription();

        PythonNetManager.Initialize(PythonNetManager.ResolveConfigPath("config.json"));

        using (Py.GIL())
        {
            Py.Import("torch");
            _np = Py.Import("numpy");
            dynamic mod = Py.Import(ModulePath);

            object? stateDictArg = (!string.IsNullOrEmpty(_modelPath) && File.Exists(_modelPath))
                                   ? _modelPath
                                   : null;
            Debug.WriteLine($"[{Name}] state_dict_path = {(stateDictArg is null ? "None (untrained)" : _modelPath)}");

            _model = mod.Model(
                state_dict_path: stateDictArg,
                num_classes:     NumClasses,
                in_h:            InH,
                in_w:            InW,
                learning_rate:   LearningRate,
                random_seed:     RandomSeed,
                is_regression:   IsRegression);

            IsTrained = stateDictArg is not null;
        }

        using (Py.GIL())
        {
            Console.WriteLine($"[{Name}] Device: {_model!.device}");
            dynamic torch = Py.Import("torch");
            Console.WriteLine($"[{Name}] CUDA available: {torch.cuda.is_available()}");
            if ((bool)torch.cuda.is_available())
                Console.WriteLine($"[{Name}] GPU: {torch.cuda.get_device_name(0)}");
        }
        var mode = IsRegression ? "regression" : "classification";
        Console.WriteLine($"[{Name}] Ultrasound predictor loaded ({mode}, {NumClasses} outputs, {InH}×{InW}): {ModelDescription}");
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_predictions.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_predictions";

    public static UltrasoundClassifier ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        if (m.Params is null || m.Params.Count < 1)
            throw new ArgumentException(
                $"[UltrasoundClassifier] '{m.Name}' requires at least Params[0]=modulePath.");

        var modulePath   = GetString(m.Params[0], "us_classifier");
        var numClasses   = m.Params.Count > 1 ? GetInt(m.Params[1], 9)             : 9;
        var inH          = m.Params.Count > 2 ? GetInt(m.Params[2], 224)           : 224;
        var inW          = m.Params.Count > 3 ? GetInt(m.Params[3], 224)           : 224;
        var learningRate = m.Params.Count > 4 ? GetDouble(m.Params[4], 1e-4)       : 1e-4;
        var randomSeed   = m.Params.Count > 5 ? GetInt(m.Params[5], 42)            : 42;
        var modelPath    = m.Params.Count > 6 ? GetString(m.Params[6], string.Empty) : string.Empty;
        var rate         = m.DesiredRate ?? 30;

        // Regression mode signalled by either a 'regression' keyword anywhere in
        // Params (e.g. "regression" or "mode:regression") or a literal bool /
        // string-bool at Params[7]. The JSON deserialiser may hand us a bool, a
        // JsonElement, or a string — handle all three.
        var isRegression = false;
        if (m.Params.Count > 7)
        {
            isRegression = ParseBoolish(m.Params[7]);
        }
        if (!isRegression)
        {
            isRegression = m.Params.Any(p =>
            {
                var s = p?.ToString() ?? "";
                return s.Equals("regression", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("mode:regression", StringComparison.OrdinalIgnoreCase);
            });
        }
        Debug.WriteLine($"[{m.Name}] ConfigureInput — isRegression={isRegression} " +
                        $"(Params[7]={(m.Params.Count > 7 ? m.Params[7]?.ToString() ?? "null" : "<missing>")})");

        var block = ActivatorUtilities.CreateInstance<UltrasoundClassifier>(
            sp, m.Name, rate, modulePath, numClasses, inH, inW, learningRate, randomSeed, modelPath, isRegression);
        return block;
    }

    /// <summary>
    /// Coerces a JSON-supplied value to a bool. Accepts native <c>bool</c>,
    /// strings <c>"true"/"false"</c>/<c>"1"/"0"</c>/<c>"yes"/"no"</c>, integer 0/1,
    /// and the string forms of all of those (which is what most JSON
    /// deserialisers give us when the target type is <c>object</c>).
    /// </summary>
    private static bool ParseBoolish(object? v)
    {
        if (v is null) return false;
        if (v is bool b) return b;
        if (v is int i) return i != 0;
        if (v is long l) return l != 0;
        var s = v.ToString()?.Trim() ?? "";
        if (s.Length == 0) return false;
        if (bool.TryParse(s, out var parsed)) return parsed;
        return s.Equals("1", StringComparison.Ordinal)
            || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || s.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static double GetDouble(object? v, double fallback)
    {
        if (v is null) return fallback;
        if (v is double d) return d;
        if (v is float f)  return f;
        if (v is int i)    return i;
        if (v is long l)   return l;
        return double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var parsed)
               ? parsed : fallback;
    }

    private void UpdateDescription()
        => ModelDescription = IsRegression
           ? $"ResNet-18 · regression · {NumClasses} outputs · {InH}×{InW}"
           : $"ResNet-18 · classification · {NumClasses} classes · {InH}×{InW}";

    // Refresh ModelDescription whenever any of its dependent properties change.
    partial void OnIsRegressionChanged(bool value) => UpdateDescription();
    partial void OnNumClassesChanged(int value)   => UpdateDescription();
    partial void OnInHChanged(int value)          => UpdateDescription();
    partial void OnInWChanged(int value)          => UpdateDescription();

    #endregion

    #region JSON export

    protected override string JsonTypeName => "USPrediction";

    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object> { ModulePath, NumClasses, InH, InW, LearningRate, RandomSeed, ModelPath, IsRegression };

    #endregion

    #region Data pipeline

    /// <summary>
    /// Routes incoming data: <see cref="TriggerBuffer"/> senders update the
    /// training buffer reference, anything else is treated as a live ultrasound
    /// frame and forwarded to <see cref="Predict"/>.
    /// </summary>
    protected override void OnReceive(object sender, object data)
    {
        if (sender is TriggerBuffer tb)
        {
            Buffer = tb;
            OnBufferSegmentAdded?.Invoke();
            return;
        }

        if (IsTraining || _model is null) return;

        try
        {
            if (data is Matrix matrix)
                Predict(matrix);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Predict error: {ex.Message}");
        }
    }

    /// <summary>
    /// Push one frame through the network and publish softmax probabilities.
    /// </summary>
    private void Predict(Matrix frame)
    {
        // Remember the live frame height so TrainIncremental can unstack the
        // buffer's vertically-stacked segments back into individual frames
        // without needing the buffer to retain its per-frame structure.
        _lastFrameH = frame.RowCount;

        Vector output;
        int    predClass;
        double predConf;

        using (Py.GIL())
        {
            dynamic frameNp   = _np!.array(frame.ToArray(), dtype: _np.float32);
            dynamic outputNp  = _model!.predict(frameNp);
            double[] values   = outputNp.reshape(-1).As<double[]>();
            output = Vector.Build.DenseOfArray(values);

            // Classification mode: argmax picks the predicted class, value at that
            // index is the softmax confidence.
            // Regression mode: outputs are independent sigmoid activations — there's
            // no "predicted class". We still surface the strongest channel and its
            // activation level so the card has something to display, but downstream
            // consumers should treat the full vector as the meaningful output.
            predClass = 0;
            predConf  = values.Length > 0 ? values[0] : 0.0;
            for (int i = 1; i < values.Length; i++)
            {
                if (values[i] > predConf) { predConf = values[i]; predClass = i; }
            }
        }

        PredictionCount++;
        PredictedClass      = predClass;
        PredictedConfidence = predConf;

        Publish(output);
        OnPrediction?.Invoke(output);

        // Throttle viz updates to ~60 fps regardless of incoming frame rate.
        var now = DateTime.UtcNow;
        if ((now - _lastVizUpdate).TotalMilliseconds >= VizUpdateIntervalMs)
        {
            _lastVizUpdate = now;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => Viz.Feed(output),
                Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    #endregion

    #region Training

    /// <summary>
    /// Train (or fine-tune) the network on labelled segments accumulated by
    /// the upstream <see cref="TriggerBuffer"/>.
    /// </summary>
    /// <param name="clusters">
    /// Each entry is one labelled recording session — <c>Label</c> is the
    /// human-readable gesture name (unused here, kept for buffer-API parity),
    /// <c>Target</c>'s first element gives the class index, and <c>Data</c>'s
    /// rows are successive captured ultrasound frames stacked vertically.
    /// </param>
    /// <param name="epochs"></param>
    public void TrainIncremental(
        List<(string Label, Matrix Targets, Matrix Data)> clusters,
        int     epochs      = 10,
        int     batchSize   = 16,
        double? lr          = null,
        double  weightDecay = 1e-5,
        double  gradientClip = 1.0,
        string? savePath    = null)
    {
        if (clusters is null || clusters.Count == 0 || _model is null) return;
        var saveTarget = savePath ?? ModelPath;

        IsTraining = true;
        OnTrainingStateChanged?.Invoke(true);

        try
        {
            // Determine the frame height from the first captured segment.
            // The upstream source publishes each B-mode frame as an (H × W) matrix; the
            // TriggerBuffer stacks them vertically so a session of N frames has
            // shape (N×H × W). We infer H by dividing the segment's row count
            // by the number of frames captured (= len of source matrices), but
            // since TriggerBuffer doesn't expose that count we use a heuristic:
            // assume all frames in a session share H, and that H matches the
            // most recent frame received in OnReceive. The block stores the
            // last observed shape so we can recover it here.
            int frameH = _lastFrameH;
            if (frameH <= 0)
            {
                Debug.WriteLine($"[{Name}] No reference frame height — has the predictor seen any live frames?");
                return;
            }

            var (frames, classLabels, regLabels) = StackClusters(clusters, frameH, IsRegression, NumClasses);
            int n = frames.Length;
            if (n == 0)
            {
                Debug.WriteLine($"[{Name}] No samples to train on.");
                return;
            }

            using (Py.GIL())
            {
                int h = frames[0].RowCount;
                int w = frames[0].ColumnCount;

                // Pack all frames into one contiguous (N, H, W) float buffer
                // and ship it across the bridge in a single numpy.array call.
                var flat = new double[n * h * w];
                for (int i = 0; i < n; i++)
                {
                    var arr = frames[i].ToRowMajorArray();
                    Array.Copy(arr, 0, flat, i * h * w, arr.Length);
                }

                dynamic shape    = new PyList(new[] { new PyInt(n), new PyInt(h), new PyInt(w) });
                dynamic framesNp = _np!.array(flat, dtype: _np.float32).reshape(shape);

                // Labels differ by mode:
                //   • Classification → (N,) int64 vector of class indices
                //   • Regression     → (N, NumClasses) float32 matrix of per-output targets
                dynamic labelsNp;
                if (IsRegression)
                {
                    dynamic labelShape = new PyList(new[] { new PyInt(n), new PyInt(NumClasses) });
                    labelsNp = _np.array(regLabels!, dtype: _np.float32).reshape(labelShape);
                    Debug.WriteLine($"[{Name}] Train (regression) — frames ({n}, {h}, {w}), labels ({n}, {NumClasses})");
                }
                else
                {
                    labelsNp = _np.array(classLabels!, dtype: _np.int64);
                    Debug.WriteLine($"[{Name}] Train (classification) — frames ({n}, {h}, {w}), labels ({classLabels!.Length})");
                }

                dynamic summary = _model!.train_incremental(
                    frames:        framesNp,
                    labels:        labelsNp,
                    epochs:        epochs,
                    batch_size:    batchSize,
                    lr:            lr.HasValue ? (object)lr.Value : null,
                    weight_decay:  weightDecay,
                    gradient_clip: gradientClip,
                    save_to:       string.IsNullOrEmpty(saveTarget) ? null : (object)saveTarget);

                // Summary dict varies slightly by mode — accuracy in classification,
                // mean output activation in regression. Both report 'loss' and 'n_samples'.
                var metricLabel = IsRegression ? "mean_act" : "acc";
                Console.WriteLine($"[{Name}] Training complete: " +
                                  $"loss={(double)summary["loss"]:F4} " +
                                  $"{metricLabel}={(double)summary[metricLabel]:F3} " +
                                  $"n={(int)summary["n_samples"]} " +
                                  $"epochs={(int)summary["epochs"]}");
            }

            IsTrained = true;
            ClearError();
        }
        catch (Exception ex)
        {
            ReportError("Training failed.", ex);
        }
        finally
        {
            IsTraining = false;
            OnTrainingStateChanged?.Invoke(false);
        }
    }

    /// <summary>
    /// Split the buffer's vertically-stacked segment data back into individual
    /// frames and produce a parallel array of integer class labels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TriggerBuffer"/> stacks every incoming matrix vertically inside
    /// a session, so a session that captured <c>N</c> frames of shape <c>H × W</c>
    /// ends up as a single <c>(N × H) × W</c> matrix. We unstack it back into
    /// <c>N</c> frames by slicing the row range <c>[k×H, (k+1)×H)</c> for each
    /// frame index <c>k</c>, using the frame height <paramref name="frameH"/>
    /// as the slice size.
    /// </para>
    /// <para>
    /// All segments in a session share the single class label stored at
    /// <c>target[0]</c> — labels are per-segment, not per-frame, because the
    /// upstream <see cref="Trigger"/> fires once per gesture.
    /// </para>
    /// </remarks>
    /// <param name="clusters">Database entries from <see cref="TriggerBuffer.dB"/>.</param>
    /// <param name="frameH">
    /// Expected height of each frame, used to slice stacked rows back into
    /// per-frame matrices. Must match the ultrasound capture's row count.
    /// </param>
    /// <param name="isRegression">When <c>true</c>, builds a row-major flat array
    /// of per-frame regression targets sized <c>N × numOutputs</c>; when <c>false</c>,
    /// builds an <c>N</c>-length array of integer class indices.</param>
    /// <param name="numOutputs">Number of regression output channels (used only
    /// when <paramref name="isRegression"/> is <c>true</c>; pads / truncates the
    /// target vector to this exact length).</param>
    /// <returns>
    /// <c>frames</c> — per-frame matrices, length N.
    /// <c>classLabels</c> — int64 class indices (null in regression mode).
    /// <c>regLabels</c> — flat float64 array of length <c>N × numOutputs</c>
    /// (null in classification mode).
    /// </returns>
    private static (Matrix[] frames, long[]? classLabels, double[]? regLabels) StackClusters(
        List<(string Label, Matrix Targets, Matrix Data)> clusters,
        int frameH, bool isRegression, int numOutputs)
    {
        var frameList      = new List<Matrix>();
        var classLabelList = isRegression ? null : new List<long>();
        var regLabelList   = isRegression ? new List<double>() : null;

        foreach (var (_, targets, data) in clusters)
        {
            if (targets.RowCount == 0 || data.RowCount == 0) continue;
            if (frameH <= 0 || data.RowCount % frameH != 0)
            {
                Debug.WriteLine($"[StackClusters] skipped segment: " +
                                $"rows={data.RowCount} not divisible by frameH={frameH}");
                continue;
            }

            int frameCount = data.RowCount / frameH;

            // The buffer guarantees one target row per data frame, so for
            // frame k the corresponding target is targets.Row(k). If the
            // counts disagree (a sender misbehaved) we fall back to row 0
            // for any out-of-range frame index — classification will still
            // work since row 0 is the active target at segment start.
            if (targets.RowCount != frameCount)
            {
                Debug.WriteLine($"[StackClusters] target/frame count mismatch: " +
                                $"targets={targets.RowCount} frames={frameCount} (will broadcast row 0)");
            }

            for (int k = 0; k < frameCount; k++)
            {
                var frame = data.SubMatrix(k * frameH, frameH, 0, data.ColumnCount);
                frameList.Add(frame);

                int t = k < targets.RowCount ? k : 0;

                if (isRegression)
                {
                    // Append numOutputs floats from this frame's target row,
                    // padding with zeros if the target is shorter than expected.
                    for (int j = 0; j < numOutputs; j++)
                    {
                        regLabelList!.Add(j < targets.ColumnCount ? targets[t, j] : 0.0);
                    }
                }
                else
                {
                    // Class index lives in column 0 of the target row.
                    var classLabel = targets.ColumnCount > 0
                                     ? (long)Math.Round(targets[t, 0])
                                     : 0L;
                    classLabelList!.Add(classLabel);
                }
            }
        }
        return (frameList.ToArray(), classLabelList?.ToArray(), regLabelList?.ToArray());
    }

    #endregion

    #region Model lifecycle (callable from UI / ViewModel)

    public void ResetModel()
    {
        if (_model is null) return;
        using (Py.GIL()) _model.reset();
        IsTrained       = false;
        PredictionCount = 0;
        Console.WriteLine($"[{Name}] Model reset to untrained state.");
    }

    public void ResetToCheckpoint()
    {
        if (_model is null) return;
        using (Py.GIL()) _model.reset_to_checkpoint();
        IsTrained       = !string.IsNullOrEmpty(ModelPath);
        PredictionCount = 0;
        Console.WriteLine($"[{Name}] Model reset to checkpoint.");
    }

    /// <summary>
    /// Save the current model state_dict to <paramref name="path"/> (or
    /// <see cref="ModelPath"/> if <paramref name="path"/> is null/empty).
    /// Updates <see cref="ModelPath"/> on success so future Load presses default
    /// to the same location.
    /// </summary>
    public void SaveModel(string? path = null)
    {
        if (_model is null) return;
        var target = string.IsNullOrWhiteSpace(path) ? ModelPath : path!;
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.WriteLine($"[{Name}] SaveModel: no path provided.");
            return;
        }
        try
        {
            using (Py.GIL()) _model.save_state_to(target);
            ModelPath = target;
            Console.WriteLine($"[{Name}] Model saved to {target}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{Name}] SaveModel failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Load a previously-saved state_dict from <paramref name="path"/> (or
    /// <see cref="ModelPath"/> if null/empty). Sets <see cref="IsTrained"/> so
    /// the card stops showing the "untrained" indicator.
    /// </summary>
    public void LoadModel(string? path = null)
    {
        if (_model is null) return;
        var target = string.IsNullOrWhiteSpace(path) ? ModelPath : path!;
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.WriteLine($"[{Name}] LoadModel: no path provided.");
            return;
        }
        if (!File.Exists(target))
        {
            Console.WriteLine($"[{Name}] LoadModel: file does not exist: {target}");
            return;
        }
        try
        {
            using (Py.GIL()) _model.load_state_from(target);
            ModelPath = target;
            IsTrained = true;
            Console.WriteLine($"[{Name}] Model loaded from {target}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{Name}] LoadModel failed: {ex.Message}");
        }
    }

    #endregion
}
