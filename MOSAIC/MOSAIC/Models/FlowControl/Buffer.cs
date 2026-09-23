using System;
using System.Collections.Generic;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// A MOSAIC block that buffers incoming <see cref="Vector{Double}"/> or <see cref="Matrix{Double}"/>
/// messages into capture segments and stores them as <c>(Target, Data)</c> pairs.
/// </summary>
/// <remarks>
/// <para>
/// This block implements a simple start/stop capture mechanism:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Capture starts when <see cref="tempFlag"/> is <c>true</c> and the block is not currently collecting.
///     The first received vector is stored as the <c>Target</c> and is not added to the data rows.
///   </description></item>
///   <item><description>
///     Subsequent received vectors are appended as data rows. If a <see cref="Matrix{Double}"/> is
///     received, each row is appended individually.
///   </description></item>
///   <item><description>
///     Capture stops when <paramref name="data"/> is <c>null</c> or <see cref="tempFlag"/> becomes <c>false</c>.
///     On stop, the buffered rows are converted to a dense matrix (one row per vector) and added to <see cref="dB"/>.
///   </description></item>
/// </list>
/// <para>
/// Note: This block does not produce an output stream; it only accumulates data into <see cref="dB"/>.
/// </para>
/// </remarks>
/// <example>
/// <para>Block entry for a larger pipeline. Params is not used. CaptureStream must send a target vector first, observation vectors or matrices next, and null to finish the segment. Results are read from dB; this block publishes no samples.</para>
/// <code language="json">
/// {
///   "Buffer": {
///     "Type": "buffer",
///     "Inputs": ["CaptureStream"]
///   }
/// }
/// </code>
/// </example>
public class Buffer : BaseBlock
{
    /// <summary>
    /// Gets the in-memory database of captured segments.
    /// </summary>
    /// <remarks>
    /// Each entry is a tuple <c>(Target, Data)</c> where:
    /// <list type="bullet">
    ///   <item><description><c>Target</c> is the first vector received when capture starts.</description></item>
    ///   <item><description><c>Data</c> is a matrix built from subsequent vectors (one vector per row).</description></item>
    /// </list>
    /// </remarks>
    public List<(Vector<double> Target, Matrix<double> Data)> dB { get; } = new();

    /// <summary>
    /// Temporarily stored data rows of the current capture segment.
    /// </summary>
    private readonly List<Vector<double>> _rows = new();

    /// <summary>
    /// The target vector for the current capture segment.
    /// </summary>
    private Vector<double>? _target;

    /// <summary>
    /// Indicates whether the block is currently collecting a capture segment.
    /// </summary>
    private bool _collecting;

    /// <summary>
    /// External gate controlling whether the buffer should capture.
    /// </summary>
    /// <remarks>
    /// When <c>false</c>, the block finalizes any ongoing capture and ignores incoming data.
    /// </remarks>
    public bool tempFlag = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="Buffer"/> class.
    /// </summary>
    public Buffer(string name, double desiredRate = 0) : base(name, desiredRate) { }

    /// <summary>
    /// Creates a <see cref="Buffer"/> instance using dependency injection and a JSON model definition.
    /// </summary>
    public static Buffer ConfigureInput(IServiceProvider sp, JsonModel m)
        => ActivatorUtilities.CreateInstance<Buffer>(sp, m.Name ?? "Buffer", 0d);

    /// <summary>
    /// Receives incoming data and performs start/stop capture logic.
    /// </summary>
    /// <param name="sender">The sender that emitted the message.</param>
    /// <param name="data">
    /// The incoming payload. Accepts <see cref="Vector{Double}"/> or <see cref="Matrix{Double}"/>.
    /// <c>null</c> ends a capture segment.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Stop condition:</b> <paramref name="data"/> is <c>null</c> or <see cref="tempFlag"/> is <c>false</c>.
    /// The current segment is finalized and appended to <see cref="dB"/>.
    /// </para>
    /// <para>
    /// <b>Start condition:</b> The first valid vector received when not collecting is stored as the target.
    /// For Matrix start data, the first row is used as the target.
    /// </para>
    /// <para>
    /// <b>Data accumulation:</b> Vectors are appended directly as rows. Matrices have each row
    /// appended individually, preserving temporal ordering.
    /// </para>
    /// </remarks>
    protected override void OnReceive(object sender, object? data)
    {
        // ── STOP ──
        if (data is null || !tempFlag)
        {
            if (_collecting)
                FinalizeSegment();
            return;
        }

        // ── Extract vector(s) from the payload ──
        switch (data)
        {
            case Vector<double> vec:
                HandleVector(vec);
                break;

            case Matrix<double> mat:
                HandleMatrix(mat);
                break;

            default:
                ReportError("Expected a vector or matrix to collect.");
                return;
        }

    }

    /// <summary>
    /// Handles a single incoming vector: either stores as target (start) or appends as data row.
    /// </summary>
    private void HandleVector(Vector<double> vec)
    {
        if (!_collecting)
        {
            // START — first vector is the target label
            _target = vec;
            _rows.Clear();
            _collecting = true;
            return;
        }

        // DATA row
        _rows.Add(vec);
    }

    /// <summary>
    /// Handles an incoming matrix: on start the first row becomes the target,
    /// remaining rows (and all rows on subsequent calls) are appended as data.
    /// </summary>
    private void HandleMatrix(Matrix<double> mat)
    {
        if (mat.RowCount == 0) return;

        if (!_collecting)
        {
            // START — first row is the target label, rest is data
            _target = mat.Row(0);
            _rows.Clear();
            _collecting = true;

            for (int r = 1; r < mat.RowCount; r++)
                _rows.Add(mat.Row(r));
            return;
        }

        // DATA — append all rows
        for (int r = 0; r < mat.RowCount; r++)
            _rows.Add(mat.Row(r));
    }

    /// <summary>
    /// Finalizes the current capture segment: builds the data matrix and appends to <see cref="dB"/>.
    /// </summary>
    private void FinalizeSegment()
    {
        var dataMatrix = _rows.Count == 0
            ? Matrix<double>.Build.Dense(0, 0)
            : Matrix<double>.Build.DenseOfRowVectors(_rows);

        dB.Add((_target ?? Vector<double>.Build.Dense(0), dataMatrix));
        _rows.Clear();
        _target = null;
        _collecting = false;
    }
}