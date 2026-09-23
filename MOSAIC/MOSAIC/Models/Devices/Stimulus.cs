using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MathNet.Numerics.LinearAlgebra;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Components.Basics;
using MOSAIC.Models.MachineLearning;
using MOSAIC.Visualization;
using static MOSAIC.Components.Basics.JsonModel;

namespace MOSAIC.Models.Devices;

/// <summary>
/// Finite-state-machine stimulus generator that produces structured sequences of target vectors.
/// </summary>
/// <remarks>
/// <para><b>Overview:</b> The Stimulus block automates what the Trigger block does manually.
/// It walks through a list of tasks (target activation vectors) with timed FSM transitions,
/// optionally shuffling the order. During the <c>capture</c> state, downstream blocks
/// (e.g. <see cref="IncrementalPredictor"/>) can record data.
/// Each tick publishes a <c>(string state, Vector&lt;double&gt; target)</c> tuple downstream.
/// </para>
///
/// <para><b>FSM States:</b></para>
/// <list type="table">
///   <listheader><term>State</term><description>Behavior</description></listheader>
///   <item><term>rest</term><description>Output zero vector for 2.0 s.</description></item>
///   <item><term>rise</term><description>Smooth sine² ramp from zero to task target over 0.5 s.</description></item>
///   <item><term>hold</term><description>Hold task target for 1.0 s.</description></item>
///   <item><term>capture</term><description>Hold task target for <c>CaptureDuration</c> s — data capture window.</description></item>
///   <item><term>fall</term><description>Smooth cosine² ramp from task target to zero over 1.0 s.</description></item>
///   <item><term>end</term><description>All tasks completed; FSM stops automatically.</description></item>
/// </list>
///
/// <para><b>CSV logging:</b> When a <c>Path</c> is specified in the JSON configuration, the base class
/// <see cref="BaseBlock.Dumper"/> automatically logs every published <c>(string, Vector&lt;double&gt;)</c>
/// tuple as <c>timestamp, state, v₀, v₁, …</c> via <see cref="CsvDumper.EnqueueLabelled"/>.</para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "Stimulus": {
///     "Type": "Stimulus",
///     "DesiredRate": 0,
///     "Inputs": [ "Timer" ],
///     "Params": [
///       "4.0",
///       "rest:0;0;0;0;0;0;0;0;0;0;0;0",
///       "power:1;1;1;1;1;1;0;0;0;0;0;0",
///       "flex:0;0;0;0;0;0;1;0;0;0;0;0",
///       "extend:0;0;0;0;0;0;0;1;0;0;0;0"
///     ],
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description>
///     <c>CaptureDuration</c> (double) — duration of the <c>capture</c> state in seconds.
///   </description></item>
///   <item><term>1..N</term><description>
///     Task definitions as <c>"name:v1;v2;v3;..."</c>. The name is used for display;
///     the semicolon-separated values form the target vector.
///     If no colon is present, the name defaults to <c>"Task[i]"</c>.
///   </description></item>
/// </list>
/// </para>
/// </example>
public sealed partial class Stimulus : BaseBlock
{
    // ══════════════════════════════════════════════════════
    //  Private fields
    // ══════════════════════════════════════════════════════

    /// <summary>Monotonic stopwatch for FSM elapsed-time tracking.</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>RNG for shuffle mode.</summary>
    private readonly Random _rand = new();

    // ── Task definitions ──

    /// <summary>Target activation vectors, one per task.</summary>
    private readonly List<Vector<double>> _tasks = new();

    /// <summary>Display names corresponding to <see cref="_tasks"/>.</summary>
    private readonly List<string> _taskNames = new();

    /// <summary>Indices of tasks not yet presented in the current run.</summary>
    private List<int> _remainingIndices = new();

    /// <summary>Index into <see cref="_remainingIndices"/> for the current task.</summary>
    private int _currentTaskIdx;

    // ── FSM timing ──

    /// <summary>Stopwatch time (seconds) at which the current state began.</summary>
    private double _transitionTime;

    /// <summary>Duration of each FSM state in seconds. <c>capture</c> is configurable via Params[0].</summary>
    private readonly Dictionary<string, double> _stateDurations = new()
    {
        ["rest"] = 2.0,
        ["rise"] = 0.5,
        ["hold"] = 1.0,
        ["capture"] = 5.0,
        ["fall"] = 1.0,
        ["end"] = 0.0
    };

    // ══════════════════════════════════════════════════════
    //  Observable properties
    // ══════════════════════════════════════════════════════

    /// <summary>Current FSM state name (rest, rise, hold, capture, fall, end).</summary>
    [ObservableProperty] private string _state = "rest";

    /// <summary>Name of the current task being presented.</summary>
    [ObservableProperty] private string _currentTaskName = "None";

    /// <summary>Current output target vector.</summary>
    [ObservableProperty] private Vector<double>? _targetValue;

    /// <summary>Whether the FSM is actively running.</summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>Whether task order is randomized each run.</summary>
    [ObservableProperty] private bool _shuffleEnabled;

    /// <summary>Number of tasks remaining in the current run.</summary>
    [ObservableProperty] private int _tasksRemaining;

    /// <summary>Total number of configured tasks.</summary>
    public int TaskCount => _tasks.Count;

    /// <summary>Duration of the capture state in seconds.</summary>
    public double CaptureDuration
    {
        get => _stateDurations["capture"];
        set => _stateDurations["capture"] = value;
    }

    /// <summary>Ordered list of task names for UI display.</summary>
    public IReadOnlyList<string> TaskNames => _taskNames;

    // ══════════════════════════════════════════════════════
    //  Visualization & Events
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// Optional visualization bundle for the target output.
    /// Set by the ViewModel; fed during FSM ticks via <see cref="BlockVisualization.Feed"/>.
    /// </summary>
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

    /// <summary>Fired when the FSM completes all tasks.</summary>
    public event Action? OnCompleted;

    /// <summary>Fired on each FSM state transition with <c>(newState, taskName)</c>.</summary>
    public event Action<string, string?>? OnFsmStateChanged;

    // ══════════════════════════════════════════════════════
    //  Constructor
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// Initializes a new <see cref="Stimulus"/> block.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="desiredRate">Processing rate in Hz (typically inherited from driving timer).</param>
    public Stimulus(string name = "Stimulus", double desiredRate = 0)
        : base(name, desiredRate)
    {
    }

    // ══════════════════════════════════════════════════════
    //  Factory / JSON
    // ══════════════════════════════════════════════════════

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_stimulus.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_stimulus";

    /// <summary>
    /// Creates a <see cref="Stimulus"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Application service provider.</param>
    /// <param name="m">
    /// JSON model with <c>Params</c>: <c>[captureDuration, "name:v1;v2;...", ...]</c>.
    /// See the class-level example for details.
    /// </param>
    /// <returns>A fully configured Stimulus block.</returns>
    public static Stimulus ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "Stimulus";
        var rate = m.DesiredRate ?? 0;

        var block = ActivatorUtilities.CreateInstance<Stimulus>(sp, name, rate);

        var parameters = m.Params ?? new List<object>();

        // First param: capture duration. A block saved with no tasks still carries it, and a config
        // with no params at all keeps the constructor default.
        if (parameters.Count > 0)
            block._stateDurations["capture"] = GetDouble(parameters[0], 5.0);

        // Remaining params: tasks as "name:v1;v2;..." or "v1;v2;..."
        for (int i = 1; i < parameters.Count; i++)
        {
            var raw = parameters[i] switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => parameters[i]?.ToString()
            };

            if (string.IsNullOrWhiteSpace(raw)) continue;

            string taskName;
            string vectorPart;

            if (raw.Contains(':'))
            {
                var parts = raw.Split(':', 2);
                taskName = parts[0].Trim();
                vectorPart = parts[1];
            }
            else
            {
                taskName = $"Task[{block._tasks.Count}]";
                vectorPart = raw;
            }

            var values = vectorPart
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
                .ToArray();

            block._tasks.Add(Vector<double>.Build.Dense(values));
            block._taskNames.Add(taskName);
        }

        Debug.WriteLine(
            $"[Stimulus '{name}'] Initialized: {block._tasks.Count} tasks, capture={block.CaptureDuration}s");
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "Stimulus";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
    {
        var list = new List<object>
        {
            CaptureDuration.ToString("G", CultureInfo.InvariantCulture)
        };

        for (int i = 0; i < _tasks.Count; i++)
        {
            var vec = _tasks[i];
            var values = string.Join(";", vec.Select(v => v.ToString("G", CultureInfo.InvariantCulture)));
            list.Add($"{_taskNames[i]}:{values}");
        }

        return list;
    }

    #endregion

    // ══════════════════════════════════════════════════════
    //  FSM Control
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// Starts the FSM, resetting task indices and transitioning to the first rest state.
    /// </summary>
    public void Start()
    {
        if (_tasks.Count == 0) return;

        _remainingIndices = Enumerable.Range(0, _tasks.Count).ToList();
        _currentTaskIdx = ShuffleEnabled ? _rand.Next(_remainingIndices.Count) : 0;
        TasksRemaining = _remainingIndices.Count;

        TransitionTo("rest");
        IsRunning = true;

        Debug.WriteLine($"[{Name}] Started ({_tasks.Count} tasks, shuffle={ShuffleEnabled})");
    }

    /// <summary>
    /// Stops the FSM immediately and resets observable state.
    /// </summary>
    public void Stop()
    {
        IsRunning = false;
        State = "rest";
        CurrentTaskName = "None";
        TargetValue = null;

        Debug.WriteLine($"[{Name}] Stopped");
    }
    
    /// <summary>
    /// Applies a custom task ordering before the FSM starts.
    /// </summary>
    public void SetTaskOrder(List<string> orderedNames)
    {
        // Build reordered parallel lists based on the requested name order
        var reorderedNames   = new List<string>();
        var reorderedVectors = new List<Vector<double>>();

        foreach (var name in orderedNames)
        {
            var index = _taskNames.IndexOf(name);
            if (index < 0) continue;

            reorderedNames.Add(_taskNames[index]);
            reorderedVectors.Add(_tasks[index]);
        }

        _taskNames.Clear();
        _taskNames.AddRange(reorderedNames);

        _tasks.Clear();
        _tasks.AddRange(reorderedVectors);
    }

    // ══════════════════════════════════════════════════════
    //  Data Pipeline (tick-driven)
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// Receives timer ticks and advances the FSM. Only processes when <see cref="IsRunning"/>.
    /// </summary>
    /// <param name="sender">The upstream clock/timer block.</param>
    /// <param name="data">Tick payload (ignored — timing is internal via <see cref="_clock"/>).</param>
    /// <remarks>
    /// <para>
    /// Each tick computes the elapsed time since the last state transition and evaluates whether
    /// the current state's duration has expired. The output vector is interpolated during
    /// <c>rise</c> (sine² ramp-up) and <c>fall</c> (cosine² ramp-down) states.
    /// </para>
    /// <para>
    /// The published tuple <c>(State, TargetValue)</c> is automatically logged to CSV
    /// by <see cref="BaseBlock.Publish"/> when <see cref="BaseBlock.Dumper"/> is configured,
    /// producing rows: <c>timestamp, state, v₀, v₁, …</c>.
    /// </para>
    /// </remarks>
    protected override void OnReceive(object sender, object data)
    {
        if (!IsRunning) return;

        var elapsed = _clock.Elapsed.TotalSeconds - _transitionTime;
        var duration = _stateDurations[State];
        var taskVec = GetCurrentTaskVector();
        var zeroVec = Vector<double>.Build.Dense(taskVec.Count, 0);

        switch (State)
        {
            case "rest":
                TargetValue = zeroVec;
                CurrentTaskName = "rest";
                if (elapsed > duration) TransitionTo("rise");
                break;

            case "rise":
                var riseProgress = Math.Min(1.0, elapsed / duration);
                TargetValue = taskVec * Math.Pow(Math.Sin(riseProgress * Math.PI / 2), 2);
                CurrentTaskName = GetCurrentTaskName();
                if (elapsed > duration) TransitionTo("hold");
                break;

            case "hold":
                TargetValue = taskVec;
                CurrentTaskName = GetCurrentTaskName();
                if (elapsed > duration) TransitionTo("capture");
                break;

            case "capture":
                TargetValue = taskVec;
                CurrentTaskName = GetCurrentTaskName();
                if (elapsed > duration) TransitionTo("fall");
                break;

            case "fall":
                var fallProgress = Math.Min(1.0, elapsed / duration);
                TargetValue = taskVec * Math.Pow(Math.Cos(fallProgress * Math.PI / 2), 2);
                CurrentTaskName = GetCurrentTaskName();
                if (elapsed > duration) AdvanceTask();
                break;

            case "end":
                Stop();
                OnCompleted?.Invoke();
                return;
        }

        if (TargetValue is not null)
        {
            Viz?.Feed(TargetValue);
            Publish((State, TargetValue));
        }
    }

    // ══════════════════════════════════════════════════════
    //  FSM Internals
    // ══════════════════════════════════════════════════════

    /// <summary>Transitions to a new FSM state and resets the elapsed timer.</summary>
    /// <param name="newState">The target FSM state name.</param>
    private void TransitionTo(string newState)
    {
        State = newState;
        _transitionTime = _clock.Elapsed.TotalSeconds;
        OnFsmStateChanged?.Invoke(newState, GetCurrentTaskName());
    }

    /// <summary>
    /// Externally forces the FSM to a specific state. Used by the TAC protocol to advance
    /// from <c>capture</c> to <c>fall</c> on trial success.
    /// </summary>
    /// <param name="stateName">Target state name (e.g. <c>"fall"</c>).</param>
    public void MoveTo(string stateName)
    {
        if (_stateDurations.ContainsKey(stateName))
            TransitionTo(stateName);
        else
            Debug.WriteLine($"[{Name}] MoveTo: unknown state '{stateName}'");
    }

    /// <summary>
    /// Advances to the next task after a fall state completes.
    /// Removes the current task from <see cref="_remainingIndices"/> and transitions to
    /// <c>rest</c> (or <c>end</c> if all tasks are done).
    /// </summary>
    private void AdvanceTask()
    {
        _remainingIndices.RemoveAt(_currentTaskIdx);
        TasksRemaining = _remainingIndices.Count;

        if (_remainingIndices.Count == 0)
        {
            TransitionTo("end");
            return;
        }

        _currentTaskIdx = ShuffleEnabled
            ? _rand.Next(_remainingIndices.Count)
            : 0;

        TransitionTo("rest");
    }

    /// <summary>Returns the target vector for the current task, or a zero vector if no tasks remain.</summary>
    /// <returns>The current task's activation vector.</returns>
    private Vector<double> GetCurrentTaskVector()
    {
        if (_remainingIndices.Count == 0 || _currentTaskIdx >= _remainingIndices.Count)
            return _tasks.Count > 0
                ? Vector<double>.Build.Dense(_tasks[0].Count, 0)
                : Vector<double>.Build.Dense(1, 0);

        return _tasks[_remainingIndices[_currentTaskIdx]];
    }

    /// <summary>Returns the display name of the current task, or <c>"None"</c> if no tasks remain.</summary>
    /// <returns>The current task name string.</returns>
    private string GetCurrentTaskName()
    {
        if (_remainingIndices.Count == 0 || _currentTaskIdx >= _remainingIndices.Count)
            return "None";
        return _taskNames[_remainingIndices[_currentTaskIdx]];
    }

    // ══════════════════════════════════════════════════════
    //  Disposal
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// Stops the FSM, disposes visualization, and releases base class resources
    /// (including the CSV dumper if configured).
    /// </summary>
    public override void Dispose()
    {
        Stop();
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