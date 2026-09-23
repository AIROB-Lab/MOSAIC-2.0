using System;
using System.Collections.Generic;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Streams a matrix frame row-by-row, emitting one <c>Vector[nChannels]</c> per clock tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Purpose:</b> Converts block-oriented matrix output (e.g., from a SlidingWindow) into a
/// continuous sample-by-sample stream at the clock rate. Each row of the matrix represents
/// one timestep; each column represents one channel. On every clock tick, the next row is
/// published as a <c>Vector[nChannels]</c>.
/// </para>
/// <para>
/// <b>Timing:</b> With a window of 200 samples, stride 100, and a 200 Hz timer:
/// <list type="bullet">
/// <item><description>The window outputs a <c>[200 × nCh]</c> matrix every 0.5 s (stride 100 / 200 Hz).</description></item>
/// <item><description>Matrix2Vector iterates 200 rows at 200 Hz = 1 second to exhaust the frame.</description></item>
/// <item><description>After 100 rows (0.5 s), the next window frame arrives and resets the cursor.</description></item>
/// <item><description>The overlap is natural — rows 100–199 of frame N are identical to rows 0–99
/// of frame N+1, so the reset produces seamless continuous output.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Dual-input design:</b>
/// <list type="bullet">
/// <item><description><b>Data source:</b> Provides frames of shape <c>(nTimesteps × nChannels)</c>.
/// Each new frame replaces the buffer and resets the row cursor.</description></item>
/// <item><description><b>Timer (identified by name via <c>Params</c>):</b> Each tick advances
/// the row cursor and publishes the next row as <c>Vector[nChannels]</c>.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Timer identification:</b> Uses the same <c>timer:&lt;name&gt;</c> convention as
/// <see cref="Joiner"/>. The sender's <see cref="BaseBlock.Name"/> is matched against
/// the configured timer name.
/// </para>
/// <para>
/// <b>Rate propagation:</b> The output rate is forced from the timer block's
/// <see cref="BaseBlock.DesiredRate"/> on the first tick, overriding any rate inherited
/// from the data source.
/// </para>
/// <para>
/// <b>JSON configuration:</b>
/// <code>
/// "stream": {
///   "Type": "Matrix2Vector",
///   "Inputs": ["timerFast", "window"],
///   "Params": ["timer:timerFast"]
/// }
/// </code>
/// </para>
/// </remarks>
public sealed partial class Matrix2Vector : BaseBlock
{
    /// <summary>Needs exactly two inputs (e.g. a data stream + a Trigger/TriggerBuffer).</summary>
    public override int MinInputs => 2;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 2;

    #region Fields

    /// <summary>Name of the upstream block whose arrival triggers row emission.</summary>
    private readonly string _timerSource;

    /// <summary>Currently buffered frame [timesteps × channels].</summary>
    private Matrix? _currentFrame;

    /// <summary>Next row index to emit on the next clock tick.</summary>
    private int _rowCursor;

    /// <summary>Lock guarding frame replacement and cursor access.</summary>
    private readonly object _frameLock = new();

    /// <summary>Whether we've already forced our rate from the timer block.</summary>
    private bool _rateLatched;

    #endregion

    #region Observable Properties

    /// <summary>Number of channels (columns) in the current frame.</summary>
    [ObservableProperty] private int _channelCount;

    /// <summary>Number of timesteps (rows) in the current frame.</summary>
    [ObservableProperty] private int _frameLength;

    /// <summary>Current row position in the frame.</summary>
    [ObservableProperty] private int _currentRow;

    /// <summary>Total vectors published.</summary>
    [ObservableProperty] private long _publishCount;

    /// <summary>Total frames received.</summary>
    [ObservableProperty] private long _frameCount;

    /// <summary>Name of the timer source (for UI display).</summary>
    [ObservableProperty] private string _timerName;

    #endregion

    #region Public Surface

    /// <summary>Visualization bundle for scope display.</summary>
    public BlockVisualization Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    #endregion

    #region Constructor & Factory

    /// <summary>
    /// Creates a new <see cref="Matrix2Vector"/> block.
    /// </summary>
    /// <param name="name">Block name.</param>
    /// <param name="desiredRate">Desired execution rate in Hz.</param>
    /// <param name="timerSource">
    /// Name of the upstream block that triggers row emission. Must not be empty.
    /// </param>
    public Matrix2Vector(string name = "Matrix2Vector", double desiredRate = 0, string timerSource = "")
        : base(name, desiredRate)
    {
        _timerSource = timerSource ?? string.Empty;
        _timerName = _timerSource;

        if (string.IsNullOrWhiteSpace(_timerSource))
            throw new ArgumentException("Matrix2Vector requires a non-empty timer source name.", nameof(timerSource));

    }

    /// <summary>
    /// Creates a <see cref="Matrix2Vector"/> from JSON pipeline configuration.
    /// </summary>
    public static Matrix2Vector ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "Matrix2Vector";
        var rate = m.DesiredRate ?? 0;

        string timer = "";
        if (m.Params is { Count: > 0 })
        {
            foreach (var o in m.Params)
            {
                var s = o?.ToString() ?? "";
                if (s.StartsWith("timer:", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("timerBlockName:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = s.Split(':', 2);
                    if (parts.Length == 2) timer = parts[1].Trim();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(timer))
            throw new ArgumentException(
                "Matrix2Vector requires 'timer:<name>' in Params (e.g., \"timer:timerFast\").");

        var block = ActivatorUtilities.CreateInstance<Matrix2Vector>(sp, name, rate, timer);
        return block;
    }

    #endregion

    #region JSON Export

    /// <inheritdoc/>
    protected override string JsonTypeName => "Matrix2Vector";

    /// <inheritdoc/>
    protected override IReadOnlyList<object>? GetJsonParams()
        => [$"timer:{_timerSource}"];

    #endregion

    #region Data Processing

    /// <inheritdoc/>
    protected override void OnReceive(object sender, object data)
    {
        // Identify sender by block name (same pattern as Joiner)
        var source = sender switch
        {
            BaseBlock block => block.Name,
            string s => s,
            _ => "unknown"
        };

        if (string.Equals(source, _timerSource, StringComparison.Ordinal))
        {
            // Timer tick → force rate from timer on first tick, then emit
            if (!_rateLatched && sender is BaseBlock timerBlock && timerBlock.DesiredRate > 0)
            {
                UpdateAndPropagateRate(timerBlock.DesiredRate);
                _rateLatched = true;
                Debug.WriteLine($"[{Name}] Rate latched from timer '{_timerSource}': {timerBlock.DesiredRate:F0} Hz");
            }

            EmitNextRow();
        }
        else
        {
            // Data frame → store
            StoreFrame(data);
        }
    }

    /// <summary>
    /// Stores an incoming frame and resets the row cursor.
    /// </summary>
    private void StoreFrame(object data)
    {
        switch (data)
        {
            case Matrix m:
                lock (_frameLock)
                {
                    _currentFrame = m;
                    _rowCursor = 0;
                    ChannelCount = m.ColumnCount;
                    FrameLength = m.RowCount;
                    CurrentRow = 0;
                }
                FrameCount++;
                break;

            case Vector v:
                // Treat a vector as a single-column matrix (N timesteps × 1 channel)
                lock (_frameLock)
                {
                    _currentFrame = Matrix.Build.DenseOfColumnVectors(v);
                    _rowCursor = 0;
                    ChannelCount = 1;
                    FrameLength = v.Count;
                    CurrentRow = 0;
                }
                FrameCount++;
                break;

            default:
                Debug.WriteLine($"[{Name}] Ignoring non-matrix/vector from '{data?.GetType().Name}'");
                break;
        }
    }

    /// <summary>
    /// Emits the next row from the current frame as a <c>Vector[nChannels]</c>.
    /// Skips silently if no frame is available or all rows have been consumed
    /// (the next frame arrival will reset the cursor).
    /// </summary>
    private void EmitNextRow()
    {
        Vector? row = null;

        lock (_frameLock)
        {
            if (_currentFrame is null || _rowCursor >= _currentFrame.RowCount)
                return;

            row = _currentFrame.Row(_rowCursor);
            _rowCursor++;
            CurrentRow = _rowCursor;
        }

        if (row is null) return;

        PublishCount++;
        Publish(row);
        Viz.Feed(row);
    }

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public override void Dispose()
    {
        Viz.Dispose();
        base.Dispose();
    }

    #endregion
}