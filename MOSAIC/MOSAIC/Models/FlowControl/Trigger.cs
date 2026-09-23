using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Models.MachineLearning;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Manual trigger block for on-demand data capture in the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> The Trigger block holds a dictionary of named activation vectors
/// (actions) and allows the user to manually start/stop capture via the UI.
/// When <see cref="StartCapture"/> is called, the current action's target vector is published
/// downstream (typically to a <see cref="Buffer"/> or <see cref="IncrementalPredictor"/> block).
/// When <see cref="StopCapture"/> is called, <see langword="null"/> is published, signalling the
/// buffer to finalise the segment.
/// </para>
/// <para>
/// <b>Oscillating mode (regression training):</b> When <see cref="OscillationMode"/> is
/// <see langword="true"/>, the published target is the action vector multiplied by
/// <c>sin²(2π·f·t)</c> at each tick of an internal timer. The active channels sweep
/// continuously from 0 to 1 and back during the recording; inactive channels stay at
/// zero. Combined with the <see cref="TriggerBuffer"/>'s per-frame target storage, this
/// gives the downstream regression model a full sweep of activation intensities for each
/// gesture in a single recording — so finger flex regression can be trained without ever
/// needing the user to provide continuous ground-truth themselves.
/// </para>
/// <para>
/// <b>sin² envelope:</b> The square is intentional — it keeps the output non-negative
/// (necessary for <c>BCEWithLogitsLoss</c> targets), and sin² has the same period as |sin|
/// but is smooth at zero crossings, avoiding the V-shape that would otherwise show up at
/// each sign change. Period = 1/f seconds; e.g. f=0.5 Hz means one full sweep every 2 s.
/// </para>
/// <para>
/// <b>Source block:</b> No upstream inputs — driven entirely by user interaction.
/// <see cref="OnReceive"/> is a no-op.
/// </para>
/// </remarks>
/// <example>
/// Classification JSON (classic, unchanged):
/// <code>
/// {
///   "Trigger": {
///     "Type": "Trigger",
///     "Params": [ "rest:0;0;0;0;0", "thumb:1;0;0;0;0", "pinch:1;1;0;0;0" ]
///   }
/// }
/// </code>
/// Regression JSON (oscillating mode, sweep at 0.5 Hz, tick at 30 Hz):
/// <code>
/// {
///   "Trigger": {
///     "Type": "Trigger",
///     "Params": [
///       "rest:0;0;0;0;0",
///       "thumb:1;0;0;0;0",
///       "pinch:1;1;0;0;0",
///       "mode:oscillating",
///       "freq:0.5",
///       "tickrate:30"
///     ]
///   }
/// }
/// </code>
/// </example>
public partial class Trigger : BaseBlock
{
    /// <summary>The Trigger is a standalone source of label/action vectors — it takes no inputs.</summary>
    public override int MinInputs => 0;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 0;

    #region State

    private bool _collecting;

    /// <summary>Timer for oscillation-mode republishing. Null in classic mode or when idle.</summary>
    private Timer? _oscillationTimer;

    /// <summary>Wall-clock anchor for the oscillation phase; resets on each <see cref="StartCapture"/>.</summary>
    private long _captureStartTicks;

    /// <summary>Lock around oscillation-timer lifecycle so Start/Stop don't race.</summary>
    private readonly object _oscillationLock = new();

    /// <summary>
    /// Most recently published activation vector. Mirrored from <see cref="BaseBlock.Publish(object)"/>
    /// calls in both classic and oscillation modes so a ViewModel can bind to it for a
    /// live bar/scope display. Null when no capture is in progress (the buffer's "stop"
    /// signal is represented here as a zero vector of the same shape, so bindings stay
    /// well-defined and the bars fall to zero on stop instead of holding the last frame).
    /// </summary>
    [ObservableProperty] private Vector<double>? _liveActivation;

    #endregion

    #region Actions dictionary

    /// <summary>Dictionary of available actions, keyed by name, with activation vectors as values.</summary>
    public Dictionary<string, Vector<double>> Actions { get; } = new();

    /// <summary>Zero-based index of the currently selected action.</summary>
    public int CurrentIdx { get; set; }

    public string CurrentActionName
    {
        get
        {
            if (Actions.Count == 0) return "No Actions";
            CurrentIdx = Math.Clamp(CurrentIdx, 0, Actions.Count - 1);
            return Actions.ElementAt(CurrentIdx).Key;
        }
    }

    public Vector<double>? CurrentActionVector
    {
        get
        {
            if (Actions.Count == 0) return null;
            CurrentIdx = Math.Clamp(CurrentIdx, 0, Actions.Count - 1);
            return Actions.ElementAt(CurrentIdx).Value;
        }
    }

    public int ActionCount => Actions.Count;

    #endregion

    #region Oscillation configuration

    /// <summary>
    /// When <see langword="true"/>, <see cref="StartCapture"/> begins an internal timer
    /// that republishes the action vector at <see cref="OscillationTickRateHz"/>, modulated
    /// by <c>sin²(2π·f·t)</c> where <c>f = <see cref="OscillationFrequencyHz"/></c>.
    /// Active channels sweep 0→1→0 continuously during the recording; inactive channels
    /// stay zero. Pairs with <see cref="TriggerBuffer"/>'s per-frame target storage to
    /// produce regression training data without continuous ground-truth input.
    /// </summary>
    public bool OscillationMode { get; set; }

    /// <summary>Sweep frequency in Hz. Default 0.5 (one full 0→1→0 cycle every 2 s).</summary>
    public double OscillationFrequencyHz { get; set; } = 0.5;

    /// <summary>
    /// How often the oscillation timer republishes the modulated target. Should match
    /// (or oversample) the data block's rate — at 30 Hz data, 30 Hz tick gives one
    /// modulated target per data frame, which is ideal alignment for the buffer's
    /// per-frame target store.
    /// </summary>
    public double OscillationTickRateHz { get; set; } = 30.0;

    #endregion

    #region Constructor & factory

    public Trigger(string name = "Trigger", double desiredRate = 0)
    {
        Name = name;
        DesiredRate = desiredRate;
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_trigger.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_trigger";

    public static Trigger ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var trigger = ActivatorUtilities.CreateInstance<Trigger>(sp, m.Name ?? "Trigger", m.DesiredRate ?? 0.0);

        var inputs = m.Inputs ?? new List<string>();
        var parameters = m.Params ?? new List<object>();

        if (inputs.Count != 0)
            throw new Exception($"Trigger {m.Name} must have no input blocks.");

        foreach (var p in parameters)
        {
            var raw = p switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var s2 = raw.Trim();

            // Oscillation-config sentinel parameters. These don't define an action;
            // they configure the block's behaviour. Recognised forms:
            //   "mode:oscillating"   or "mode:classic"
            //   "freq:0.5"           (Hz)
            //   "tickrate:30"        (Hz)
            // Anything else with a ':' is treated as a name:vector action definition.
            if (TryParseConfigParam(s2, trigger)) continue;

            if (!s2.Contains(':'))
                throw new Exception($"Invalid action: '{s2}'. Expected 'name:v1;v2;...' or 'mode:oscillating' / 'freq:F' / 'tickrate:F'");

            var parts = s2.Split(':', 2);
            var actionName = parts[0].Trim();
            var values = parts[1]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => double.Parse(t, CultureInfo.InvariantCulture))
                .ToArray();

            trigger.Actions[actionName] = Vector<double>.Build.Dense(values);
        }

        Debug.WriteLine(
            $"[Trigger '{trigger.Name}'] Initialized with {trigger.Actions.Count} actions, " +
            $"mode={(trigger.OscillationMode ? "oscillating" : "classic")}" +
            (trigger.OscillationMode ? $", freq={trigger.OscillationFrequencyHz} Hz, tick={trigger.OscillationTickRateHz} Hz" : ""));
        return trigger;
    }

    /// <summary>
    /// Parse a config-style parameter ("mode:...", "freq:...", "tickrate:..."). Returns
    /// <see langword="true"/> if the parameter was a config directive (and applied);
    /// <see langword="false"/> if it should be treated as a regular action definition.
    /// </summary>
    private static bool TryParseConfigParam(string s, Trigger t)
    {
        var idx = s.IndexOf(':');
        if (idx <= 0) return false;
        var key = s.Substring(0, idx).Trim().ToLowerInvariant();
        var val = s.Substring(idx + 1).Trim();

        switch (key)
        {
            case "mode":
                if (val.Equals("oscillating", StringComparison.OrdinalIgnoreCase)
                 || val.Equals("oscillate",   StringComparison.OrdinalIgnoreCase)
                 || val.Equals("sine",        StringComparison.OrdinalIgnoreCase))
                {
                    t.OscillationMode = true;
                    return true;
                }
                if (val.Equals("classic", StringComparison.OrdinalIgnoreCase)
                 || val.Equals("constant", StringComparison.OrdinalIgnoreCase)
                 || val.Equals("static",   StringComparison.OrdinalIgnoreCase))
                {
                    t.OscillationMode = false;
                    return true;
                }
                return false; // unrecognised mode token → let it be parsed as an action

            case "freq":
            case "frequency":
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0)
                {
                    t.OscillationFrequencyHz = f;
                    return true;
                }
                return false;

            case "tickrate":
            case "tick":
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0)
                {
                    t.OscillationTickRateHz = r;
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    #endregion

    #region JSON Export

    protected override string JsonTypeName => "Trigger";

    protected override IReadOnlyList<object>? GetJsonParams()
    {
        if (Actions.Count == 0) return null;

        var list = new List<object>();
        foreach (var kvp in Actions)
        {
            var values = string.Join(";", kvp.Value.Select(v => v.ToString("G", CultureInfo.InvariantCulture)));
            list.Add($"{kvp.Key}:{values}");
        }
        // Emit oscillation config only when non-default, so classic Trigger JSON
        // round-trips unchanged.
        if (OscillationMode)
        {
            list.Add("mode:oscillating");
            list.Add($"freq:{OscillationFrequencyHz.ToString("G", CultureInfo.InvariantCulture)}");
            list.Add($"tickrate:{OscillationTickRateHz.ToString("G", CultureInfo.InvariantCulture)}");
        }
        return list;
    }

    #endregion

    #region OnReceive

    protected override void OnReceive(object sender, object data)
    {
        // Source block — no upstream input.
    }

    #endregion

    #region Action navigation

    public void NextAction()
    {
        if (Actions.Count == 0) return;
        CurrentIdx = (CurrentIdx + 1) % Actions.Count;
        OnPropertyChanged(nameof(CurrentActionName));
    }

    public void PreviousAction()
    {
        if (Actions.Count == 0) return;
        CurrentIdx = (CurrentIdx - 1 + Actions.Count) % Actions.Count;
        OnPropertyChanged(nameof(CurrentActionName));
    }

    public void SelectAction(string name)
    {
        int idx = 0;
        foreach (var kvp in Actions)
        {
            if (kvp.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                CurrentIdx = idx;
                OnPropertyChanged(nameof(CurrentActionName));
                return;
            }
            idx++;
        }
    }

    #endregion

    #region Capture lifecycle

    /// <summary>
    /// Begin a capture segment. In classic mode, publishes the current action's vector
    /// once and stays silent for the rest of the segment. In oscillation mode, kicks off
    /// an internal timer that republishes the vector at <see cref="OscillationTickRateHz"/>,
    /// modulated by sin²(2π·f·t) — sweeping the action's active channels from 0 to 1 and
    /// back continuously until <see cref="StopCapture"/>.
    /// </summary>
    public void StartCapture()
    {
        if (_collecting) StopCapture();
        if (Actions.Count == 0) return;
        var vec = CurrentActionVector;
        if (vec is null) return;

        _collecting = true;

        if (OscillationMode)
        {
            // Anchor the phase to "now" so the sweep starts at sin²(0) = 0 and ramps in
            // smoothly. Then start a periodic timer that pumps modulated targets to the
            // buffer at the configured tick rate.
            _captureStartTicks = Stopwatch.GetTimestamp();
            Debug.WriteLine(
                $"[Trigger '{Name}'] Start oscillating capture: {CurrentActionName} " +
                $"@ {OscillationFrequencyHz} Hz, tick {OscillationTickRateHz} Hz");

            // Emit the initial (zero-modulation) target so the buffer opens a segment
            // with a valid Vector. Subsequent ticks will keep updating _currentTarget
            // on the buffer's side.
            PublishModulated(vec, phaseSeconds: 0.0);

            var periodMs = Math.Max(1, (int)Math.Round(1000.0 / Math.Max(1.0, OscillationTickRateHz)));
            lock (_oscillationLock)
            {
                _oscillationTimer?.Dispose();
                _oscillationTimer = new Timer(OscillationTick, vec, periodMs, periodMs);
            }
        }
        else
        {
            Debug.WriteLine($"[Trigger '{Name}'] Start capture: {CurrentActionName}");
            Publish(vec);
            LiveActivation = vec;
        }
    }

    /// <summary>
    /// End the current capture segment. Stops the oscillation timer (if running) and
    /// publishes <see langword="null"/> so the buffer finalises the segment.
    /// </summary>
    public void StopCapture()
    {
        lock (_oscillationLock)
        {
            _oscillationTimer?.Dispose();
            _oscillationTimer = null;
        }

        Debug.WriteLine($"[Trigger '{Name}'] Stop capture");
        Publish(null);
        // Zero out the live readout (without setting to null, which would unbind
        // the AXAML and cause the bars to vanish entirely — we want them to fall
        // to zero gracefully).
        var prev = LiveActivation;
        if (prev is not null)
            LiveActivation = Vector<double>.Build.Dense(prev.Count, 0.0);
        _collecting = false;
    }

    /// <summary>
    /// Timer callback — computes the sin²-modulated target for the elapsed phase and
    /// publishes it. Bound to the action vector captured at <see cref="StartCapture"/>
    /// so a mid-recording <see cref="SelectAction"/> doesn't change which gesture is
    /// being recorded.
    /// </summary>
    private void OscillationTick(object? state)
    {
        if (!_collecting) return;
        if (state is not Vector<double> baseVec) return;

        double elapsedSec = (Stopwatch.GetTimestamp() - _captureStartTicks)
                          / (double)Stopwatch.Frequency;
        PublishModulated(baseVec, elapsedSec);
    }

    /// <summary>
    /// Compute the modulated target <c>baseVec · sin²(2π·f·t)</c> and publish it.
    /// </summary>
    private void PublishModulated(Vector<double> baseVec, double phaseSeconds)
    {
        double s = Math.Sin(2.0 * Math.PI * OscillationFrequencyHz * phaseSeconds);
        double envelope = s * s; // sin² ∈ [0, 1], smooth at zero crossings
        var modulated = baseVec.Clone();
        for (int i = 0; i < modulated.Count; i++)
            modulated[i] *= envelope;
        Publish(modulated);
        LiveActivation = modulated;
    }

    #endregion

    #region Disposal

    public override void Dispose()
    {
        lock (_oscillationLock)
        {
            _oscillationTimer?.Dispose();
            _oscillationTimer = null;
        }
        base.Dispose();
    }

    #endregion
}