using System;
using System.Threading;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Visualization.Heatmap;

namespace MOSAIC.Visualization;

/// <summary>
/// All-in-one visualization bundle for any processing block.
/// Owns a ScopeMonitor, SpiderMonitor, and HeatMapMonitor, and provides
/// a single <see cref="Feed(Vector{double})"/> call to update all of them.
/// </summary>
public sealed class BlockVisualization : IDisposable
{
    #region Properties

    /// <summary>
    /// Global switch for whether new <see cref="BlockVisualization"/> instances build their
    /// underlying chart monitors. Always <c>true</c> in the running app. Unit tests set this to
    /// <c>false</c> (from an <c>[AssemblyInitialize]</c>) so that constructing a block does not
    /// spin up LiveCharts UI controls — which are pointless without a window and whose one-time
    /// global initialization is not thread-safe under parallel test execution.
    /// </summary>
    public static bool Enabled = true;

    private readonly bool _enabled = Enabled;
    private readonly object _monitorLock = new();
    private volatile ScopeMonitor.ScopeMonitor? _scope;
    private volatile SpiderMonitor.SpiderMonitor? _spider;
    private volatile HeatMapMonitor? _heatmap;

    /// <summary>Created on first UI request, never as a side effect of feeding data.</summary>
    public ScopeMonitor.ScopeMonitor? Scope
    {
        get
        {
            lock (_monitorLock)
            {
                if (!_enabled || _disposed) return null;
                if (_scope is null)
                {
                    _scope = new ScopeMonitor.ScopeMonitor();
                    if (EffectiveSignalRate > 0) _scope.UpdateSignalRate(EffectiveSignalRate);
                }
                return _scope;
            }
        }
    }
    public SpiderMonitor.SpiderMonitor? Spider
    {
        get { lock (_monitorLock) return !_enabled || _disposed ? null : _spider ??= new SpiderMonitor.SpiderMonitor(); }
    }
    public HeatMapMonitor? Heatmap
    {
        get
        {
            lock (_monitorLock)
            {
                if (!_enabled || _disposed) return null;
                if (_heatmap is null) { _heatmap = new HeatMapMonitor(); _heatmap.UseGlobalAuto(); }
                return _heatmap;
            }
        }
    }

    #endregion

    #region Visibility tracking

    private int _attachedCount;
    public bool IsVisible => _attachedCount > 0;

    public void NotifyAttached() => Interlocked.Increment(ref _attachedCount);
    public void NotifyDetached() => Interlocked.Decrement(ref _attachedCount);

    #endregion

    #region Lifecycle

    private bool _disposed;

    public BlockVisualization() { }

    public void Dispose()
    {
        lock (_monitorLock)
        {
            if (_disposed) return;
            _disposed = true;
            _scope?.Dispose();
            _spider?.Dispose();
            _heatmap?.Dispose();
        }
    }

    #endregion

    #region Signal Rate

    private double _signalRate;
    private double _desiredRate;
    private double _publishRate;
    private double _derivedRate;
    private int _rowsPerFeed;

    /// <summary>
    /// The rate the owning block publishes at, pushed automatically by <c>BaseBlock</c>.
    /// </summary>
    /// <remarks>
    /// Not the same thing as the rate the scope needs: the scope advances its x-axis once per row
    /// written, and one publish may carry many rows. See <see cref="RefreshDerivedRate"/>.
    /// </remarks>
    public double PublishRate => _publishRate;

    /// <summary>
    /// The sample rate last declared through <see cref="UpdateSignalRate"/>, or 0 if none was.
    /// </summary>
    /// <remarks>
    /// This is the explicit declaration only. A value of 0 is normal when publication rate and
    /// feed shape supply the time base automatically. Read <see cref="EffectiveSignalRate"/>
    /// to check either path, including without a chart attached. If neither path supplies a
    /// positive rate, the scope falls back to its display window and buffer size.
    /// </remarks>
    public double SignalRate => _signalRate;

    /// <summary>The publish rate last declared through <see cref="UpdateDesiredRate"/>, or 0 if none was.</summary>
    public double DesiredRate => _desiredRate;

    /// <summary>
    /// Informs the scope monitor of the upstream signal rate so it can size
    /// its internal buffers and time-axis correctly.
    /// A non-positive value restores automatic derivation from publication rate and feed shape.
    /// </summary>
    public void UpdateSignalRate(double signalRate)
    {
        if (_disposed) return;
        _signalRate = signalRate;
        if (signalRate > 0)
        {
            _scope?.UpdateSignalRate(signalRate);
        }
        else
        {
            // Rate and shape may have changed while the explicit declaration suppressed
            // derivation. Recalculate now; another feed with the same shape would not do it.
            RefreshDerivedRate(_rowsPerFeed);
            _scope?.UpdateSignalRate(_derivedRate);
        }
    }

    /// <summary>
    /// Sets the block's output rate (Hz). Used together with the signal rate to
    /// extract only the non-overlapping tail from windowed matrices in
    /// <see cref="Feed(Matrix{double})"/>.
    /// </summary>
    public void UpdateDesiredRate(double desiredRate)
    {
        if (_disposed) return;
        _desiredRate = desiredRate;
    }

    /// <summary>
    /// Tells this visualization how often its block publishes. Pushed automatically by
    /// <c>BaseBlock</c> whenever the block's rate changes; blocks do not call this themselves.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="UpdateDesiredRate"/>, which means the block's output
    /// rate for the overlap trim in <see cref="Feed(Matrix{double})"/> and has only a handful of
    /// callers. Feeding every block's rate into that instead would arm the trim across the whole
    /// catalogue and start discarding rows that are currently plotted.
    /// </remarks>
    public void UpdatePublishRate(double publishRate)
    {
        if (_disposed || Math.Abs(_publishRate - publishRate) < 0.001) return;
        _publishRate = publishRate;
        RefreshDerivedRate(_rowsPerFeed);
    }

    /// <summary>
    /// Derives the scope's time base from the publish rate and the size of each feed, for blocks
    /// that never declare a sample rate of their own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scope advances its x-axis by one sample period per row written, so the rate it needs is
    /// rows per second: <c>publishRate × rowsPerFeed</c>. One row per publish makes that the publish
    /// rate; a block emitting a matrix of N rows per publish makes it N times higher.
    /// </para>
    /// <para>
    /// This exists because almost nothing declared a rate. Without it the scope fell back to a
    /// period derived from its own window and buffer size — a fixed 5 ms, i.e. an assumed 200 Hz —
    /// which is unrelated to the signal and wrong in both directions: a 2 kHz source raced ten
    /// times too fast, while a SlopeSignChanges behind a stride-25 window at 8 Hz crawled, taking
    /// 25 seconds of wall clock to draw one second of trace.
    /// </para>
    /// <para>
    /// An explicit <see cref="UpdateSignalRate"/> always wins. Some packetized sources publish
    /// batches whose row count varies with transport coalescing, so those declare their hardware
    /// sample rate directly.
    /// </para>
    /// </remarks>
    private void RefreshDerivedRate(int rows)
    {
        if (_disposed || _signalRate > 0) return;       // an explicit declaration wins
        if (_publishRate <= 0 || rows <= 0) return;     // not enough known yet

        _derivedRate = _publishRate * rows;
        _scope?.UpdateSignalRate(_derivedRate);
    }

    /// <summary>
    /// The rate worked out from the publish rate and the feed size, or 0 before both are known.
    /// </summary>
    public double DerivedSignalRate => _derivedRate;

    /// <summary>
    /// The rate the scope is actually running on — the explicit declaration when a block made one,
    /// otherwise the derived rate.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="SignalRate"/> so "a block declared this" and "we worked it
    /// out" stay distinguishable: only the former suppresses the derivation.
    /// </remarks>
    public double EffectiveSignalRate => _signalRate > 0 ? _signalRate : _derivedRate;

    #endregion

    #region Feed — Vector

    /// <summary>
    /// Records how many rows a feed carries, so the time base can be derived from it.
    /// </summary>
    /// <remarks>
    /// Called before the visibility check: a scope that is not on screen yet still needs the right
    /// period the moment it is shown, and the shape is known now. Costs a comparison per feed once
    /// the shape has settled, which it does after the first one.
    /// </remarks>
    private void NoteFeedShape(int rows)
    {
        if (rows <= 0 || _rowsPerFeed == rows) return;
        _rowsPerFeed = rows;
        RefreshDerivedRate(rows);
    }

    /// <summary>Feed one sample (Vector) to all three monitors.</summary>
    public void Feed(Vector<double>? data)
    {
        if (_disposed || data is null) return;
        NoteFeedShape(1);
        if (!IsVisible) return;

        _scope?.EnqueueData(data);
        _spider?.EnqueueData(data);
        _heatmap?.EnqueueFrame(data);
    }

    /// <summary>Feed one sample (ReadOnlySpan) to all three monitors.</summary>
    public void Feed(ReadOnlySpan<double> data)
    {
        if (_disposed) return;
        NoteFeedShape(1);
        if (!IsVisible) return;

        if (_scope?.AcceptsData == true || _spider?.IsActive == true)
        {
            var vec = Vector<double>.Build.Dense(data.ToArray());
            _scope?.EnqueueData(vec);
            _spider?.EnqueueData(vec);
        }
        _heatmap?.EnqueueFrame(data);
    }

    #endregion

    #region Feed — Matrix

    /// <summary>
    /// Feed an entire batch matrix [rows × channels].
    /// When signal rate and desired rate are known, only the non-overlapping
    /// tail of the matrix is sent to the scope so that overlapping windows
    /// from a <c>SlidingWindow</c> don't produce jagged re-plots.
    /// Spider and Heatmap receive only the last row — they're envelope-style
    /// displays where intermediate frames within one 30 fps tick aren't meaningful.
    /// </summary>
    public void Feed(Matrix<double>? matrix)
    {
        if (_disposed || matrix is null) return;

        int rows = matrix.RowCount;
        int cols = matrix.ColumnCount;
        if (rows == 0 || cols == 0) return;

        int retainedRows = rows;
        if (_signalRate > 0 && _desiredRate > 0 && _signalRate > _desiredRate)
            retainedRows = Math.Min(rows, Math.Max(1, (int)Math.Round(_signalRate / _desiredRate)));

        // Metadata is cheap and remains current even while the view is hidden.
        NoteFeedShape(retainedRows);
        if (!IsVisible) return;

        if (_scope?.AcceptsData == true)
        {
            var scopeMatrix = retainedRows == rows ? matrix
                : matrix.SubMatrix(rows - retainedRows, retainedRows, 0, cols);
            _scope.EnqueueBatch(scopeMatrix);
        }
        if (_spider?.IsActive == true || _heatmap?.IsActive == true)
        {
            var lastRow = matrix.Row(rows - 1);
            _spider?.EnqueueData(lastRow);
            _heatmap?.EnqueueFrame(lastRow);
        }
    }

    #endregion
}
