using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Tests.TAC;
using MOSAIC.Models.Devices;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Visualization;
using static MOSAIC.Components.Basics.JsonModel;

namespace MOSAIC.Models.Tests;

/// <summary>
/// Target Achievement Control (TAC) test block.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reference:</b> Simon et al. 2011 —
/// <see href="https://pmc.ncbi.nlm.nih.gov/articles/PMC4232230/"/>.
/// </para>
/// <para>
/// <b>Overview:</b> Evaluates a user's ability to reach target postures by computing
/// L2 distance between prediction and target on each tick. A <see cref="DwellDetector"/>
/// tracks whether the distance stays below threshold for the required dwell time.
/// Trial-level and aggregate metrics are managed by <see cref="Components.Tests.TAC.TACResults"/>.
/// </para>
/// <para>
/// <b>Inputs:</b> Requires exactly two upstream connections:
/// <list type="bullet">
///   <item><description>A <see cref="Stimulus"/> block — provides target vectors and FSM
///     state via <c>(string, Vector&lt;double&gt;)</c> tuples.</description></item>
///   <item><description>A prediction source (e.g.
///     <see cref="MOSAIC.Models.MachineLearning.IncrementalPredictor"/>) — provides
///     <c>Vector&lt;double&gt;</c> predictions.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Results:</b> Trial-level and aggregate metrics are managed by the companion
/// <see cref="Components.Tests.TAC.TACResults"/> class, accessible via <see cref="Results"/>.
/// Use <see cref="Components.Tests.TAC.TACResults.ExportCsv"/> for full trial-level export.
/// </para>
/// <para>
/// <b>Stimulus binding:</b> The pipeline builder must call <see cref="BindStimulus"/> after
/// all blocks are constructed. Auto-binding is attempted if the sender is a
/// <see cref="Stimulus"/> instance.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "TAC": {
///     "Type": "TAC",
///     "Inputs": [ "Stimulus", "Predictor" ],
///     "Params": [ "Stimulus", 2.0, 0.15 ]
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description><c>StimulusBlockName</c> (string) — name of the Stimulus block to bind to.</description></item>
///   <item><term>1</term><description><c>DwellTime</c> (double, default 2.0) — required continuous hold duration in seconds.</description></item>
///   <item><term>2</term><description><c>SuccessThreshold</c> (double, default 0.2) — maximum L2 distance to count as "in target".</description></item>
/// </list>
/// </para>
/// </example>
public sealed partial class TAC : BaseBlock
{
    /// <summary>Needs exactly two inputs (e.g. a data stream + a Trigger/TriggerBuffer).</summary>
    public override int MinInputs => 2;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 2;

    /// <summary>High-resolution stopwatch for trial timing.</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Dwell-time state machine tracking hold duration and overshoots.</summary>
    private readonly DwellDetector _dwell;

    /// <summary>Total number of <see cref="Evaluate"/> calls (for diagnostic throttling).</summary>
    private int _evaluateCount;

    /// <summary>Bound Stimulus block reference (set via <see cref="BindStimulus"/>).</summary>
    private Stimulus? _stimulus;

    /// <summary>Name of the Stimulus block to bind to.</summary>
    private readonly string _stimulusBlockName;

    /// <summary>Current target vector from the Stimulus.</summary>
    private Vector<double>? _target;

    /// <summary>Current prediction vector from the upstream predictor.</summary>
    private Vector<double>? _prediction;

    /// <summary>Previous prediction vector (for path length computation).</summary>
    private Vector<double>? _prevPrediction;

    /// <summary>Timestamp when the current trial started.</summary>
    private double _trialStartTime;

    /// <summary>Whether a trial is currently active.</summary>
    private bool _trialActive;

    /// <summary>Cumulative path length for the current trial.</summary>
    private double _trialPathLength;

    /// <summary>Initial L2 distance at trial start.</summary>
    private double _trialInitialDistance;

    /// <summary>Last FSM state received from the data stream.</summary>
    private string _lastReceivedState = "idle";

    /// <summary>Previous Stimulus FSM state (for transition detection).</summary>
    private string _lastStimulusState = "";

    /// <summary>Task name of the current trial.</summary>
    private string _currentTrialTaskName = "";

    /// <summary>Required continuous hold duration in seconds.</summary>
    public double DwellTime { get; }

    /// <summary>Maximum L2 distance to count as "in target".</summary>
    public double SuccessThreshold { get; }

    /// <summary>Name of the Stimulus block to bind to (for pipeline builder).</summary>
    public string StimulusBlockName => _stimulusBlockName;

    /// <summary>
    /// Trial-level and aggregate metrics. Bind in the ViewModel for UI display
    /// and use <see cref="Components.Tests.TAC.TACResults.ExportCsv"/> for export.
    /// </summary>
    public Components.Tests.TAC.TACResults Results { get; }

    /// <summary>Visualization bundle. Set by the ViewModel.</summary>
    private BlockVisualization? _viz;

    /// <summary>Live plot for this block. Assigned by the ViewModel, which owns the scope.</summary>
    /// <remarks>
    /// Setting this re-pushes the publish rate: the rate is normally pushed when it changes,
    /// which for these blocks happens during construction — before the ViewModel has handed
    /// over the scope — so without this the scope would never learn its time base.
    /// </remarks>
    public BlockVisualization? Viz
    {
        get => _viz;
        set { _viz = value; RefreshVisualizationRate(); }
    }

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>Whether the user is actively attempting to reach the target.</summary>
    [ObservableProperty] private bool _isTrying;

    /// <summary>Current L2 distance between prediction and target.</summary>
    [ObservableProperty] private double _distanceToTarget;

    /// <summary>Continuous time spent within the success threshold.</summary>
    [ObservableProperty] private double _timeInTarget;

    /// <summary>Elapsed time since the current trial started.</summary>
    [ObservableProperty] private double _trialElapsed;

    /// <summary>Name of the current task (from the Stimulus).</summary>
    [ObservableProperty] private string _currentTaskName = "None";

    /// <summary>Current Stimulus FSM state.</summary>
    [ObservableProperty] private string _stimulusState = "idle";

    /// <summary>Overshoot count for the current trial.</summary>
    [ObservableProperty] private int _currentOvershootCount;

    /// <summary>Hold progress towards dwell time [0, 1].</summary>
    [ObservableProperty] private double _holdProgress;

    /// <summary>Fired when a trial completes (success, completionTime, efficiency, overshoots).</summary>
    public event Action<bool, double, double, int>? OnTrialCompleted;

    /// <summary>Fired on each evaluation tick (distance, timeInTarget, inTarget).</summary>
    public event Action<double, double, bool>? OnEvaluation;

    /// <summary>
    /// Initializes a new <see cref="TAC"/> block.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="desiredRate">Processing rate in Hz (typically inherited).</param>
    /// <param name="stimulusBlockName">Name of the Stimulus block to bind to.</param>
    /// <param name="dwellTime">Required continuous hold duration in seconds.</param>
    /// <param name="successThreshold">Maximum L2 distance for target achievement.</param>
    public TAC(string name, double desiredRate, string stimulusBlockName,
        double dwellTime = 2.0, double successThreshold = 0.2)
        : base(name, desiredRate)
    {
        _stimulusBlockName = stimulusBlockName;
        DwellTime = dwellTime;
        SuccessThreshold = successThreshold;
        _dwell = new DwellDetector(dwellTime, successThreshold);
        Results = new Components.Tests.TAC.TACResults(name, dwellTime, successThreshold);
        Debug.WriteLine($"[{Name}] TAC created: dwell={DwellTime}s, threshold={SuccessThreshold}");
    }

    /// <summary>
    /// Creates a <see cref="TAC"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">JSON model. See the class-level example for <c>Params</c> layout.</param>
    /// <returns>A configured <see cref="TAC"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown if required parameters are missing.</exception>
    public static TAC ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "TAC";
        var rate = m.DesiredRate ?? 0;
        var p = m.Params;

        return ActivatorUtilities.CreateInstance<TAC>(sp, name, rate, 
            GetString(p?[0], "Stimulus") ?? "Stimulus", 
            GetDouble(p?[1], 2.0),
            GetDouble(p?[2], 0.2));

    }

    /// <inheritdoc />
    protected override string JsonTypeName => "TAC";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
        => new object[] { _stimulusBlockName, DwellTime, SuccessThreshold };

    /// <summary>
    /// Binds the TAC to a <see cref="Stimulus"/> block. Must be called by the pipeline builder
    /// after all blocks are constructed. Without this, trial detection will not work.
    /// </summary>
    /// <param name="stimulus">The Stimulus block instance.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stimulus"/> is <see langword="null"/>.</exception>
    public void BindStimulus(Stimulus stimulus)
    {
        if (_stimulus is not null) return;
        _stimulus = stimulus ?? throw new ArgumentNullException(nameof(stimulus));
        _stimulus.OnCompleted += OnStimulusCompleted;
        Debug.WriteLine($"[{Name}] *** BOUND to Stimulus '{_stimulus.Name}' ***");
    }

    /// <summary>Logs summary when the Stimulus finishes all tasks.</summary>
    private void OnStimulusCompleted()
    {
        Debug.WriteLine($"[{Name}] Stimulus COMPLETED. Final: " +
                        $"{Results.TrialsCompleted}/{Results.TrialsAttempted} ({Results.CompletionRate:F0}%)");
    }

    /// <summary>
    /// Dispatches incoming data: Stimulus inputs provide targets, other inputs provide predictions.
    /// Evaluation runs whenever both target and prediction are available.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="data">
    /// A <c>(string, Vector&lt;double&gt;)</c> tuple from a Stimulus, or a
    /// <see cref="Vector{T}"/> prediction from a predictor.
    /// </param>
    protected override void OnReceive(object sender, object data)
    {
        try
        {
            if (sender is Stimulus stim)
            {
                if (_stimulus is null) BindStimulus(stim);

                (_lastReceivedState, _target) = data switch
                {
                    ValueTuple<string, Vector<double>> t => (t.Item1, t.Item2),
                    Vector<double> sv => (_lastReceivedState, sv),
                    _ => (_lastReceivedState, _target)
                };
            }
            else
            {
                _prediction = data switch
                {
                    Vector<double> v => v,
                    ValueTuple<string, Vector<double>> t => t.Item2,
                    _ => _prediction
                };
            }

            if (_target is not null && _prediction is not null)
                Evaluate();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] OnReceive ERROR: {ex}");
        }
    }

    /// <summary>
    /// Computes L2 distance, delegates dwell tracking to <see cref="DwellDetector"/>,
    /// and detects trial boundaries from Stimulus FSM state transitions.
    /// </summary>
    private void Evaluate()
    {
        var now = _clock.Elapsed.TotalSeconds;

        if (++_evaluateCount <= 5 || _evaluateCount % 500 == 0)
            Debug.WriteLine($"[{Name}] Evaluate #{_evaluateCount}: stimState={_stimulus?.State ?? "?"}");

        // Align vector lengths
        if (_target!.Count != _prediction!.Count)
        {
            var n = Math.Min(_target.Count, _prediction.Count);
            _target = _target.SubVector(0, n);
            _prediction = _prediction.SubVector(0, n);
        }

        DistanceToTarget = (_target - _prediction).L2Norm();

        // Track path length
        if (_trialActive && _prevPrediction is not null && _prevPrediction.Count == _prediction.Count)
            _trialPathLength += (_prediction - _prevPrediction).L2Norm();
        _prevPrediction = _prediction;

        // Read stimulus state
        var stimState = _stimulus?.State ?? _lastReceivedState;
        CurrentTaskName = _stimulus?.CurrentTaskName ?? "Unknown";
        StimulusState = stimState;

        // Trial boundaries on state change
        if (stimState != _lastStimulusState)
        {
            HandleStateTransition(stimState, now);
            _lastStimulusState = stimState;
        }

        IsTrying = stimState == "capture" && _trialActive;

        // Dwell tracking during active capture
        if (_trialActive && stimState == "capture")
        {
            TrialElapsed = now - _trialStartTime;
            _dwell.Update(DistanceToTarget, now);

            TimeInTarget = _dwell.HoldTime;
            HoldProgress = _dwell.HoldProgress;
            CurrentOvershootCount = _dwell.Overshoots;

            if (_dwell.IsComplete)
            {
                CompleteTrialSuccess(now - _trialStartTime);
                return;
            }
        }
        else if (!_trialActive)
        {
            TimeInTarget = 0;
            HoldProgress = 0;
        }

        Viz?.Feed(Vector<double>.Build.Dense(new[] { DistanceToTarget, SuccessThreshold }));
        Publish(DistanceToTarget);
        OnEvaluation?.Invoke(DistanceToTarget, _dwell.HoldTime, _dwell.InTarget);
    }

    /// <summary>Routes state transitions to <see cref="StartTrial"/> or <see cref="CompleteTrialFailure"/>.</summary>
    private void HandleStateTransition(string newState, double now)
    {
        if (newState == "capture" && _lastStimulusState != "capture")
            StartTrial(now);
        else if (_lastStimulusState == "capture" && newState != "capture" && _trialActive)
            CompleteTrialFailure();
    }

    /// <summary>Initialises per-trial state when the Stimulus enters <c>"capture"</c>.</summary>
    private void StartTrial(double now)
    {
        Results.TrialsAttempted++;
        _trialActive = true;
        _trialStartTime = now;
        _trialPathLength = 0;
        _trialInitialDistance = (_target is not null && _prediction is not null)
            ? (_target - _prediction).L2Norm()
            : 0;
        _prevPrediction = _prediction;
        _currentTrialTaskName = _stimulus?.CurrentTaskName ?? "Unknown";
        _dwell.Reset();
        ResetTrialUI();

        Debug.WriteLine($"[{Name}] ▶ Trial #{Results.TrialsAttempted} STARTED: " +
                        $"'{_currentTrialTaskName}' (d₀={_trialInitialDistance:F3})");
    }

    /// <summary>Records a successful trial, plays a beep, and advances the Stimulus FSM to <c>"fall"</c>.</summary>
    private void CompleteTrialSuccess(double completionTime)
    {
        _trialActive = false;
        Results.TrialsCompleted++;

        double eff = (_trialPathLength > 1e-9 && _trialInitialDistance > 1e-9)
            ? Math.Min(1.0, _trialInitialDistance / _trialPathLength)
            : 0;

        Results.RecordTrial(new TrialRecord(
            Results.TrialsAttempted, _currentTrialTaskName,
            true, completionTime, _trialInitialDistance, _trialPathLength, eff,
            _dwell.Overshoots, _target));

        Debug.WriteLine($"[{Name}] ✓ Trial #{Results.TrialsAttempted} SUCCESS in {completionTime:F2}s " +
                        $"(eff={eff:F2}, os={_dwell.Overshoots})");

        Task.Run(() =>
        {
            try
            {
                Console.Beep(1000, 300);
            }
            catch
            {
                /* no-op */
            }
        });
        _stimulus?.MoveTo("fall");
        OnTrialCompleted?.Invoke(true, completionTime, eff, _dwell.Overshoots);
        ResetTrialUI();
    }

    /// <summary>Records a failed trial when the Stimulus leaves <c>"capture"</c> before dwell completion.</summary>
    private void CompleteTrialFailure()
    {
        _trialActive = false;

        Results.RecordTrial(new TrialRecord(
            Results.TrialsAttempted, _currentTrialTaskName,
            false, 0, _trialInitialDistance, _trialPathLength, 0,
            _dwell.Overshoots, _target));

        Debug.WriteLine($"[{Name}] ✗ Trial #{Results.TrialsAttempted} FAILED (os={_dwell.Overshoots})");
        OnTrialCompleted?.Invoke(false, 0, 0, _dwell.Overshoots);
        ResetTrialUI();
    }

    /// <summary>Resets observable UI properties between trials.</summary>
    private void ResetTrialUI()
    {
        TimeInTarget = 0;
        HoldProgress = 0;
        TrialElapsed = 0;
        CurrentOvershootCount = 0;
    }

    /// <summary>
    /// Unbinds the Stimulus, disposes visualization, and releases base class resources.
    /// </summary>
    public override void Dispose()
    {
        if (_stimulus is not null)
            _stimulus.OnCompleted -= OnStimulusCompleted;
        try
        {
            Viz?.Dispose();
        }
        catch
        {
            /* no-op */
        }

        base.Dispose();
    }
}
