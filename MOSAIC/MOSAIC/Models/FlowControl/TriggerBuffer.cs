using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Captures incoming data windows into labelled segments, controlled by a
/// trigger input. Stores both classification and regression labels uniformly
/// as a per-frame <see cref="Matrix"/> of target values.
/// </summary>
/// <remarks>
/// <para>
/// Requires exactly two inputs in the pipeline JSON:
/// <list type="number">
///   <item><description>
///     A trigger block (first input). Sending a <see cref="Vector"/> starts /
///     updates the active target; sending <c>null</c> stops the segment and
///     finalises it. The sender is identified by class name containing
///     <c>"Trigger"</c>.
///   </description></item>
///   <item><description>
///     A data provider (second input, e.g. <c>SlidingWindow</c> or
///     <c>MockUltrasoundSource</c>). While capture is
///     active, each published <see cref="Matrix"/> or <see cref="Vector"/> is
///     accumulated as one data row in the segment.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Target shape — uniform across classification and regression.</b>
/// Each entry in <see cref="dB"/> has a <see cref="Matrix"/> <c>Targets</c>
/// with one row per data row. For the classic click-driven <c>Trigger</c>
/// block (which publishes the target Vector once on start), the buffer caches
/// it as <see cref="_currentTarget"/> and queues an identical row for every
/// data frame received during the segment — so classification consumers can
/// read <c>Targets.Row(0)</c> and ignore the rest. For a streaming trigger
/// (<c>StreamTrigger</c>, future <c>DataGlove</c>, etc.) that publishes a new
/// target Vector each tick, the buffer queues whichever target is most recent
/// at the moment each data row arrives — so regression consumers see a true
/// per-frame target stream.
/// </para>
/// </remarks>
/// <example>
/// <para>Block entry for a larger pipeline. Params is not used. Capture must be a Trigger-compatible block that provides targets and start/stop events. Features supplies the matching observations. Completed segments can be used for batch training.</para>
/// <code language="json">
/// {
///   "TriggerBuffer": {
///     "Type": "triggerbuffer",
///     "Inputs": ["Features", "Capture"]
///   }
/// }
/// </code>
/// </example>
public class TriggerBuffer : BaseBlock
{
    /// <summary>Needs exactly two inputs (e.g. a data stream + a Trigger/TriggerBuffer).</summary>
    public override int MinInputs => 2;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 2;

    #region State

    /// <summary>
    /// Most recently received target vector. Held between trigger messages so
    /// that data frames arriving in between get labelled with the latest known
    /// target — this is what makes classic single-publish triggers and
    /// per-tick streaming triggers transparently interchangeable.
    /// </summary>
    private Vector? _currentTarget;

    /// <summary>One row per captured data frame; finalised into a Matrix on stop.</summary>
    private readonly List<Vector> _targetRows = new();

    /// <summary>The data frames themselves; stacked vertically on stop.</summary>
    private readonly List<Matrix> _matrices = new();

    /// <summary>Active recording flag — toggled by trigger start / stop.</summary>
    private bool _collecting;

    /// <summary>Human-readable name attached to the segment, when known.</summary>
    private string _currentLabel = string.Empty;

    #endregion

    #region Database

    /// <summary>
    /// All captured segments as <c>(Label, Targets, Data)</c> triples.
    /// <c>Targets.RowCount</c> equals the number of data frames received; the
    /// row at index <c>k</c> is the target that was active when data frame
    /// <c>k</c> arrived. For classification, every row is identical and
    /// consumers can use <c>Targets.Row(0)</c>; for regression, rows vary
    /// across time.
    /// </summary>
    public List<(string Label, Matrix Targets, Matrix Data)> dB { get; } = new();

    #endregion

    #region Constructor & Factory

    public TriggerBuffer(string name) : base(name, desiredRate: 0) { }

    public static TriggerBuffer ConfigureInput(IServiceProvider sp, JsonModel m)
        => ActivatorUtilities.CreateInstance<TriggerBuffer>(sp, m.Name ?? "TriggerBuffer");

    #endregion

    #region JSON Export

    protected override string JsonTypeName => "TriggerBuffer";
    protected override IReadOnlyList<object>? GetJsonParams() => null;

    #endregion

    #region Data Pipeline

    protected override void OnReceive(object sender, object? data)
    {
        // The sender's class name decides whether this is a trigger message or
        // a data frame. Same pattern as IncrementalPredictor uses — any block
        // whose type-name contains "Trigger" counts (so StreamTrigger,
        // ManualTrigger, etc. all work without buffer modifications).
        var senderName = sender?.GetType().Name ?? "";
        var isTrigger  = senderName == "Trigger" || senderName.Contains("Trigger");

        if (isTrigger)
        {
            HandleTrigger(sender, data, senderName);
            return;
        }

        // Data path — only accumulate while a segment is in progress and we
        // have a target to pair with each frame.
        if (!_collecting || _currentTarget is null) return;

        if (data is Matrix m)
        {
            _matrices.Add(m);
            _targetRows.Add(_currentTarget);
        }
        else if (data is Vector v)
        {
            // Wrap single vector as a single-row matrix for uniform storage.
            _matrices.Add(v.ToRowMatrix());
            _targetRows.Add(_currentTarget);
        }
    }

    private void HandleTrigger(object? sender, object? data, string senderName)
    {
        if (data is Vector label)
        {
            // Update target — covers both the "start" message (first vector
            // received) and per-tick updates from a streaming trigger.
            _currentTarget = label;
            if (!_collecting) StartCapture(label, senderName, sender);
        }
        else if (data is null)
        {
            StopCapture();
        }
    }

    private void StartCapture(Vector firstLabel, string senderName, object? sender)
    {
        _currentTarget = firstLabel;
        _matrices.Clear();
        _targetRows.Clear();
        _collecting   = true;
        _currentLabel = ResolveSegmentLabel(sender, senderName);
        Console.WriteLine(
            $"[{Name}] Capture started — label='{_currentLabel}', target=[{FormatVector(firstLabel)}]");
    }

    /// <summary>
    /// Pick a human-readable label for the segment from whichever trigger-like
    /// block sent the start message. The classic <c>Trigger</c> block exposes
    /// <c>CurrentActionName</c> (e.g. "THUMB"); a future <c>StreamTrigger</c>
    /// exposes <c>SessionName</c> instead. Try both via reflection so the buffer
    /// doesn't need a compile-time reference to either type; fall back to the
    /// sender's class name only if neither property exists or is empty.
    /// </summary>
    private static string ResolveSegmentLabel(object? sender, string fallbackTypeName)
    {
        if (sender is null) return fallbackTypeName;
        var t = sender.GetType();

        // Names checked in order of preference. Trigger uses CurrentActionName,
        // StreamTrigger uses SessionName, anything else might still expose Name
        // or Label as a generic property.
        foreach (var propName in new[] { "CurrentActionName", "SessionName", "Label", "Name" })
        {
            var prop = t.GetProperty(propName);
            var raw  = prop?.GetValue(sender)?.ToString();
            if (!string.IsNullOrWhiteSpace(raw) && raw != "No Actions")
                return raw;
        }
        return fallbackTypeName;
    }

    private void StopCapture()
    {
        if (!_collecting) return;
        _collecting = false;

        if (_matrices.Count == 0 || _targetRows.Count == 0)
        {
            Console.WriteLine($"[{Name}] Capture stopped — no data, segment discarded.");
            ResetSegmentState();
            return;
        }

        // Stack data frames vertically (same as before).
        var data = _matrices[0];
        for (int i = 1; i < _matrices.Count; i++)
            data = data.Stack(_matrices[i]);

        // Stack target vectors vertically into a (N × outputDim) matrix.
        // Classification: every row identical. Regression: rows vary.
        var targets = StackVectorsAsRows(_targetRows);

        dB.Add((_currentLabel, targets, data));
        Publish(dB.Count);
        Console.WriteLine(
            $"[{Name}] Segment saved — {_matrices.Count} frames, " +
            $"targets={targets.RowCount}×{targets.ColumnCount}, " +
            $"data={data.RowCount}×{data.ColumnCount}, total segments={dB.Count}");

        ResetSegmentState();
    }

    private void ResetSegmentState()
    {
        _matrices.Clear();
        _targetRows.Clear();
        _currentTarget = null;
        _currentLabel  = string.Empty;
    }

    /// <summary>
    /// Stack a list of equal-length vectors into a single matrix with one row
    /// per vector. Handles the case where vectors have differing lengths by
    /// padding shorter rows with zeros up to the widest vector seen.
    /// </summary>
    private static Matrix StackVectorsAsRows(List<Vector> rows)
    {
        int n = rows.Count;
        int d = 0;
        for (int i = 0; i < n; i++) if (rows[i].Count > d) d = rows[i].Count;
        if (d == 0) return Matrix.Build.Dense(0, 0);

        var m = Matrix.Build.Dense(n, d);
        for (int i = 0; i < n; i++)
        {
            var row = rows[i];
            for (int j = 0; j < row.Count && j < d; j++)
                m[i, j] = row[j];
        }
        return m;
    }

    private static string FormatVector(Vector v)
        => string.Join(",", v.Select(x => x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));

    #endregion

    #region Utilities

    /// <summary>Clears all captured segments and resets state.</summary>
    public void Clear()
    {
        if (_collecting) StopCapture();
        dB.Clear();
        Console.WriteLine($"[{Name}] Buffer cleared.");
    }

    /// <summary>
    /// Removes a single segment from <see cref="dB"/> by index. Bounds-checked;
    /// silently no-ops if the index is out of range (e.g. the UI raced with a
    /// concurrent <see cref="Clear"/>). Does NOT republish <see cref="dB"/>'s
    /// count — downstream consumers only react to additions, and a deletion
    /// shouldn't force them to retrain mid-session.
    /// </summary>
    /// <param name="index">Zero-based index in <see cref="dB"/>.</param>
    /// <returns><see langword="true"/> if a segment was removed.</returns>
    public bool RemoveSegment(int index)
    {
        if (index < 0 || index >= dB.Count) return false;
        var (label, targets, data) = dB[index];
        dB.RemoveAt(index);
        Console.WriteLine(
            $"[{Name}] Segment {index + 1} removed ('{label}', " +
            $"targets={targets.RowCount}×{targets.ColumnCount}, " +
            $"data={data.RowCount}×{data.ColumnCount}). " +
            $"Remaining: {dB.Count}");
        return true;
    }

    /// <summary>
    /// Save all segments to CSV. Each row of output is one data frame with
    /// the corresponding target vector prepended:
    /// <c>label, t0, t1, …, tD-1, d0, d1, …, dC-1</c>.
    /// </summary>
    public void SaveToCsv(string filePath)
    {
        using var writer = new StreamWriter(filePath);
        foreach (var (label, targets, data) in dB)
        {
            // Per-frame target — falls back to row 0 if the target matrix is
            // shorter than the data matrix (defensive; shouldn't happen).
            for (int r = 0; r < data.RowCount; r++)
            {
                int targetRow = r < targets.RowCount ? r : 0;
                var targetStr = string.Join(",",
                    targets.Row(targetRow).Select(v => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)));
                var rowStr = string.Join(",",
                    data.Row(r).Select(v => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)));
                writer.WriteLine($"{label},{targetStr},{rowStr}");
            }
        }
        Console.WriteLine($"[{Name}] Saved {dB.Count} segments to {filePath}");
    }

    public override void Dispose()
    {
        dB.Clear();
        base.Dispose();
    }

    #endregion
}
