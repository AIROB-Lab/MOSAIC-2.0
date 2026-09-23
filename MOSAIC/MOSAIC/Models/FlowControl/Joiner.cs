﻿using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;

namespace MOSAIC.Models;

/// <summary>
/// Joiner: expects inputs from multiple sources.
/// When the configured "timer" source arrives, concatenates all latest values
/// and publishes one <see cref="Vector{double}"/>.
/// </summary>
/// <example>
/// <para>Block entry for a larger pipeline. The timer token is required and must name an input. Each SignalA arrival publishes the latest concatenated values in Inputs order. This does not synchronize acquisition between sources.</para>
/// <code language="json">
/// {
///   "Joiner": {
///     "Type": "joiner",
///     "Inputs": ["SignalA", "SignalB"],
///     "Params": ["timer:SignalA"]
///   }
/// }
/// </code>
/// </example>
public sealed class Joiner : BaseBlock
{
    private string _timerSource;
    private readonly Dictionary<string, object?> _latest = new(StringComparer.Ordinal);

    /// <summary>
    /// Guards <see cref="_latest"/>. Every declared input is published by its OWN block, and
    /// every block owns its own <c>OutputDispatcher</c> with its own pump thread — so a Joiner
    /// with seven inputs has seven threads writing this dictionary concurrently. Unsynchronised
    /// concurrent adds can corrupt a <see cref="Dictionary{TKey,TValue}"/>'s bucket chain, which
    /// shows up as a hang inside a resize rather than an exception, and the window for it is
    /// startup — exactly when the keys are being created.
    /// </summary>
    /// <remarks>
    /// It also makes <see cref="ConcatenateLatest"/> a consistent snapshot. That method walks
    /// the sources twice, once to sum lengths and once to fill, and a writer landing between the
    /// two passes can make the second disagree with the first — for an undeclared source
    /// appearing mid-publish, by enough to run off the end of the destination array.
    /// </remarks>
    private readonly object _latestLock = new();

    /// <summary>
    /// Blocks that have published, but will never fill a slot in <see cref="_latest"/> under their
    /// own name — so waiting for them to do so would mean waiting forever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two ways in. A block whose payload is neither a Vector nor a Matrix: a Clock publishes a
    /// bare timestamp, and four shipped configs list one among a Joiner's inputs. And a block using
    /// the legacy tuple format, which files its value under a tag of its own choosing rather than
    /// its block name.
    /// </para>
    /// <para>
    /// Both are excluded from the wait in <see cref="AllInputsSeen"/> — without which a single such
    /// input holds the gate shut for the life of the process and the Joiner publishes nothing at
    /// all, silently. This changes only what the block waits for, never what it emits:
    /// <see cref="OrderedLatest"/> is driven entirely by what is present in <see cref="_latest"/>.
    /// </para>
    /// </remarks>
    private readonly HashSet<string> _notAwaited = new(StringComparer.Ordinal);
    // Expose current timer source name
    public string TimerSourceName => _timerSource;

    // Allow VM to change the timer source at runtime
    public void SetTimerSource(string sourceName) => _timerSource = sourceName;

    /// <summary>What <c>ConfigureInput</c> reads back: the timer source, in the <c>timer:</c> form it accepts.</summary>
    protected override IReadOnlyList<object>? GetJsonParams() => [$"timer:{_timerSource}"];

    /// <summary>
    /// Declared inputs still to deliver their first joinable value. Empty once every source that
    /// can contribute one has spoken, which is also when this block starts publishing.
    /// </summary>
    /// <remarks>
    /// Inputs that cannot contribute — see <see cref="_notAwaited"/> — never appear here, so a
    /// Joiner listing a Clock among its inputs is not held up by it.
    /// </remarks>
    public IReadOnlyList<string> PendingInputs { get; private set; } = [];

    /// <summary>Whether output is being withheld until every declared input has delivered once.</summary>
    public bool IsWaitingForInputs => PendingInputs.Count > 0;

    public Joiner(string name = "Joiner", double desiredRate = 0, string timerSource = "") : base(name, desiredRate)
    {
        _timerSource = timerSource ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_timerSource))
            throw new ArgumentException("Joiner requires a non-empty timer source name.", nameof(timerSource));
    }
    /// <inheritdoc />
    public override int MinInputs => 2;
    /// <inheritdoc />
    public override int MaxInputs => int.MaxValue;

    /// <summary>
    /// JSON config: Params supports "timerBlockName:Name" or "timer:Name".
    /// </summary>
    public static Joiner ConfigureInput(IServiceProvider sp, JsonModel m) 
    {
        var name = m.Name ?? "Joiner";
        var rate = m.DesiredRate ?? 0;

        string timer = "";
        if (m.Params is { Count: > 0 })
        {
            foreach (var o in m.Params)
            {
                var s = o?.ToString() ?? "";
                if (s.StartsWith("timerBlockName:", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("timer:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = s.Split(':', 2);
                    if (parts.Length == 2) timer = parts[1].Trim();
                }
            }
        }
        if (string.IsNullOrWhiteSpace(timer))
            throw new ArgumentException("Joiner requires 'timerBlockName:<Name>' (or 'timer:<Name>') in Params.");

        return ActivatorUtilities.CreateInstance<Joiner>(sp, name, rate, timer);
    }

    /// <summary>
    /// Accepts both legacy (string, object) tuples and standard BaseBlock outputs.
    /// </summary>
    protected override void OnReceive(object sender, object data)
    {
        string source;
        object payload;

        // The block that actually published, whatever key the value ends up filed under. Needed
        // because a declared input is named after the block, so that is the name the gate below
        // waits on — see _notAwaited.
        string senderName = sender switch
        {
            BaseBlock block => block.Name,
            string s => s,
            _ => "unknown"
        };

        // Handle legacy tuple format: (string source, object data)
        if (data is ValueTuple<string, object> tuple)
        {
            source = tuple.Item1;
            payload = tuple.Item2;
        }
        // Handle standard BaseBlock format: sender is the source block
        else
        {
            source = senderName;
            payload = data;
        }

        // Only support Vector<double> / Matrix<double>
        if (payload is not Vector<double> && payload is not Matrix<double>)
        {
            lock (_latestLock) _notAwaited.Add(senderName);
            return;
        }

        Vector<double> snapshot;
        string? log;

        lock (_latestLock)
        {
            _latest[source] = payload;

            // Joinable, but filed under a tag of the sender's choosing rather than its own name —
            // the legacy tuple format, used by SiFi ("imu"/"ppg"), Stimulus (its state) and
            // OnlineLDA (the class label). The declared input is named after the block, so that
            // name never appears in _latest and the gate must not sit waiting for it. The value
            // still reaches the output: OrderedLatest emits anything no declared name claimed.
            if (!string.Equals(source, senderName, StringComparison.Ordinal))
                _notAwaited.Add(senderName);

            if (!string.Equals(source, _timerSource, StringComparison.Ordinal))
                return;

            // Publish complete records only. A declared source that has never delivered
            // contributes no group at all (see OrderedLatest), so the vector comes out short and
            // every source after the gap lands in a slot belonging to someone else. Downstream
            // that is invisible: a BodyRig bound its torso to an upper-arm IMU and reported
            // itself healthy. Waiting for the first frame from every source pins slot k to
            // declared input k for the whole run, because from then on a source that goes quiet
            // keeps its last value and its width.
            if (!AllInputsSeen(out log))
            {
                Report(log);
                return;
            }

            snapshot = ConcatenateLatest();
        }

        // Both of these are deliberately outside the lock.
        //
        // Publish dispatches into every subscriber's mailbox; holding the lock across it would
        // let one slow downstream block stall all of this Joiner's producer pumps, converting a
        // consumer hiccup into upstream backpressure. Console writes are synchronous on Windows
        // and cost milliseconds, which is not something to hold a lock through either.
        Report(log);
        Publish(snapshot);
    }

    /// <summary>Writes a state-change line, if <see cref="AllInputsSeen"/> produced one.</summary>
    private static void Report(string? line)
    {
        if (line is not null) Console.WriteLine(line);
    }

    /// <summary>
    /// Whether every declared input has delivered at least once, refreshing
    /// <see cref="PendingInputs"/> as the answer changes.
    /// </summary>
    /// <remarks>
    /// A Joiner with no declared inputs (built in code rather than from JSON) has nothing to
    /// align against and keeps the old arrival-order behaviour.
    /// </remarks>
    /// <param name="log">
    /// A line to write once the caller has released <see cref="_latestLock"/>, or
    /// <see langword="null"/> when the answer has not changed since the last tick.
    /// </param>
    private bool AllInputsSeen(out string? log)
    {
        log = null;

        var declared = Inputs;
        if (declared is null || declared.Count == 0) return true;

        List<string>? pending = null;
        foreach (var name in declared)
            if (!_latest.ContainsKey(name) && !_notAwaited.Contains(name))
                (pending ??= []).Add(name);

        if (pending is null)
        {
            if (PendingInputs.Count > 0)
            {
                PendingInputs = [];
                log = $"[{Name}] All inputs present - publishing.";
            }
            return true;
        }

        // Only when the set changes: the pacer ticks at the device rate, and logging every tick
        // would bury the one line that matters under hundreds a second.
        if (!pending.SequenceEqual(PendingInputs, StringComparer.Ordinal))
        {
            PendingInputs = pending;
            log = $"[{Name}] Waiting for first data from: {string.Join(", ", pending)}";
        }

        return false;
    }

    /// <summary>
    /// The sources to concatenate, in a stable order.
    /// </summary>
    /// <remarks>
    /// Declared input order when the block was configured from JSON, falling back to arrival
    /// order only for sources the configuration does not mention. Iterating the dictionary
    /// directly would concatenate in first-arrival order, so the segment a given sensor lands
    /// on would depend on which packet happened to appear first and could differ between runs —
    /// invisible with one input, silently swapping channels with two.
    /// </remarks>
    private IEnumerable<object?> OrderedLatest()
    {
        var declared = Inputs;
        if (declared is null || declared.Count == 0)
        {
            foreach (var v in _latest.Values) yield return v;
            yield break;
        }

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in declared)
        {
            if (!emitted.Add(name)) continue;
            if (_latest.TryGetValue(name, out var value)) yield return value;
        }

        foreach (var pair in _latest)
            if (!emitted.Contains(pair.Key)) yield return pair.Value;
    }

    private Vector<double> ConcatenateLatest()
    {
        // 1) total length
        var total = 0;
        foreach (var v in OrderedLatest())
        {
            if (v is Vector<double> vec) total += vec.Count;
            else if (v is Matrix<double> mat) total += mat.RowCount * mat.ColumnCount;
        }
        if (total == 0) return DenseVector.Create(0, 0.0);

        // 2) flatten row-major
        var dst = new double[total];
        var off = 0;

        foreach (var v in OrderedLatest())
        {
            if (v is Vector<double> vec)
            {
                for (int i = 0; i < vec.Count; i++) dst[off + i] = vec[i];
                off += vec.Count;
            }
            else if (v is Matrix<double> mat)
            {
                for (int r = 0; r < mat.RowCount; r++)
                {
                    var row = mat.Row(r);
                    for (int c = 0; c < row.Count; c++) dst[off + c] = row[c];
                    off += row.Count;
                }
            }
        }

        return DenseVector.Build.DenseOfArray(dst);
    }
}