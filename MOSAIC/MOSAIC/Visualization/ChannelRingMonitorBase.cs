using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Components.Basics;
using MOSAIC.Services;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.Visualization;

/// <summary>
/// Shared data engine for the time-series (ring-backed) monitors. Owns everything that was
/// previously copy-pasted across ScopeMonitor / SpectrogramMonitor / etc.:
/// <list type="bullet">
///   <item>the lock-free <see cref="SampleRingBuffer"/> ingest (<see cref="EnqueueData"/> / <see cref="EnqueueBatch"/>),</item>
///   <item>the <see cref="VisualizationTimer"/> subscription and <see cref="Pause"/>/<see cref="Resume"/> lifecycle,</item>
///   <item>the coalesced flush (<c>Interlocked</c> + <c>Dispatcher.Post</c>),</item>
///   <item>the coalesced channel reconfigure, and</item>
///   <item>the monotonic clock + optional accept-rate throttle.</item>
/// </list>
/// A subclass supplies only its rendering: <see cref="OnFlush"/>, <see cref="OnRebuild"/>,
/// <see cref="OnResetData"/>, <see cref="UpdateThemeColors"/>, and (optionally) state-reset and
/// readiness hooks.
/// </summary>
public abstract class ChannelRingMonitorBase : ObservableObject, IMonitor, IDisposable
{
    // ── Lifecycle flags (protected so subclasses can guard their own UI work) ──
    protected volatile bool IsDisposed;
    protected volatile bool IsActive;
    internal bool AcceptsData => IsActive;
    protected volatile bool IsUpdating;

    private int _flushScheduled;
    private int _reconfigureScheduled;
    private int _pendingChannelCount;

    private int _numberOfChannels;
    /// <summary>Current channel count (frame width). Set on the UI thread via reconfigure / configure.</summary>
    public int ChannelCount => _numberOfChannels;

    private readonly Action _timerCallback;
    private VisualizationTimer.TickRate _tickRate = VisualizationTimer.TickRate.Fps30;

    // ── Ring ingest ──
    private SampleRingBuffer? _ring;
    private double[] _scratch = Array.Empty<double>();

    /// <summary>The ingest ring for the current channel count, or null when there are no channels.</summary>
    protected SampleRingBuffer? Ring => _ring;

    /// <summary>Frames of ingest headroom. Must exceed (rate / flushRate) and the largest EnqueueBatch.</summary>
    protected virtual int RingCapacityFrames => 8192;

    /// <summary>Upper bound on channel count. Defaults to 128.</summary>
    protected virtual int MaxChannels => 128;

    // ── Optional accept-rate throttle (null = accept every sample) ──
    /// <summary>When non-null, <see cref="EnqueueData"/> drops samples that arrive closer together
    /// than this interval (decimation gate). <see cref="EnqueueBatch"/> is never throttled.</summary>
    protected virtual TimeSpan? AcceptInterval => null;
    private DateTime _lastAcceptedUtc = DateTime.MinValue;

    // ── Mirror sinks ──
    // Secondary monitors that receive an exact copy of every ingest call BEFORE this monitor's own
    // active/throttle gates, so a mirror keeps rendering even while this (source) monitor is paused
    // (e.g. its inline card is scrolled off-screen). Used by the aggregate Scope Monitor window to
    // replay a block's real scope feed without re-deriving it from the block's published output.
    // Copy-on-write array: registration happens on the UI thread, forwarding on the producer thread.
    private volatile ChannelRingMonitorBase[] _mirrors = Array.Empty<ChannelRingMonitorBase>();
    private readonly object _mirrorLock = new();

    /// <summary>The currently registered mirror sinks (snapshot; safe to read off-thread).</summary>
    protected ChannelRingMonitorBase[] Mirrors => _mirrors;

    /// <summary>Registers a monitor to receive a verbatim copy of this monitor's ingest.
    /// A mirror must not itself be a source with mirrors (no re-forwarding), which is the case for the
    /// aggregate window's leaf monitors.</summary>
    public void AddMirror(ChannelRingMonitorBase mirror)
    {
        if (mirror is null || ReferenceEquals(mirror, this)) return;
        lock (_mirrorLock)
        {
            if (Array.IndexOf(_mirrors, mirror) >= 0) return;
            var next = new ChannelRingMonitorBase[_mirrors.Length + 1];
            Array.Copy(_mirrors, next, _mirrors.Length);
            next[^1] = mirror;
            _mirrors = next;
        }
    }

    /// <summary>Unregisters a previously added mirror sink. Safe to call on a disposed monitor.</summary>
    public void RemoveMirror(ChannelRingMonitorBase mirror)
    {
        if (mirror is null) return;
        lock (_mirrorLock)
        {
            int idx = Array.IndexOf(_mirrors, mirror);
            if (idx < 0) return;
            if (_mirrors.Length == 1) { _mirrors = Array.Empty<ChannelRingMonitorBase>(); return; }
            var next = new ChannelRingMonitorBase[_mirrors.Length - 1];
            Array.Copy(_mirrors, 0, next, 0, idx);
            Array.Copy(_mirrors, idx + 1, next, idx, _mirrors.Length - idx - 1);
            _mirrors = next;
        }
    }

    /// <summary>Whether the render surface is ready to draw. The base skips flushing while false.</summary>
    protected virtual bool IsRenderReady => true;

    // ── Monotonic clock ──
    private readonly long _t0Stamp = Stopwatch.GetTimestamp();
    private readonly long _t0UtcTicks = DateTime.UtcNow.Ticks;
    private long _lastMonotonicTicks;

    protected ChannelRingMonitorBase()
    {
        _timerCallback = RequestFlush;
        _lastMonotonicTicks = _t0UtcTicks;
    }

    #region Configuration

    /// <summary>Display refresh rate. Pauses/resumes the timer subscription around the change.</summary>
    public VisualizationTimer.TickRate TickRate
    {
        get => _tickRate;
        set
        {
            if (_tickRate == value) return;
            var wasActive = IsActive;
            if (wasActive) Pause();
            _tickRate = value;
            if (wasActive) Resume();
        }
    }

    #endregion

    #region Data input

    /// <summary>Enqueues one multi-channel sample. Safe to call from the producer thread.</summary>
    public void EnqueueData(Vector data)
    {
        if (IsDisposed || data is null || data.Count == 0) return;

        // Fan out to mirrors first, before our own active/throttle gates, so a paused source
        // (e.g. off-screen inline card) still feeds an active mirror in the aggregate window.
        var mirrors = _mirrors;
        for (int i = 0; i < mirrors.Length; i++) mirrors[i].EnqueueData(data);

        if (!IsActive) return;

        var ring = _ring; // capture once — reconfigure may swap it on the UI thread
        if (ring is null || ring.Channels != data.Count)
        {
            RequestChannelReconfigure(data.Count);
            return;
        }

        var interval = AcceptInterval;
        if (interval.HasValue)
        {
            var now = NowMonotonicUtc();
            if (now - _lastAcceptedUtc < interval.Value) return;
            _lastAcceptedUtc = now;
        }

        var arr = data.AsArray(); // zero-copy for dense vectors
        if (arr is not null)
        {
            ring.Write(arr);
        }
        else
        {
            if (_scratch.Length != data.Count) _scratch = new double[data.Count];
            for (int i = 0; i < _scratch.Length; i++) _scratch[i] = data[i];
            ring.Write(_scratch);
        }
    }

    /// <summary>Enqueues a [rows × channels] matrix. Bypasses the throttle; decimates to maxRowsToKeep.</summary>
    public void EnqueueBatch(Matrix matrix, int maxRowsToKeep = 4096)
    {
        if (IsDisposed || matrix is null) return;

        // Fan out to mirrors first (see EnqueueData). Each mirror applies its own decimation.
        var mirrors = _mirrors;
        for (int i = 0; i < mirrors.Length; i++) mirrors[i].EnqueueBatch(matrix, maxRowsToKeep);

        if (!IsActive) return;

        int rows = matrix.RowCount;
        int cols = matrix.ColumnCount;
        if (rows == 0 || cols == 0) return;

        var ring = _ring;
        if (ring is null || ring.Channels != cols)
        {
            RequestChannelReconfigure(cols);
            return;
        }

        int stride = rows > maxRowsToKeep ? (int)Math.Ceiling((double)rows / maxRowsToKeep) : 1;

        if (_scratch.Length != cols) _scratch = new double[cols];
        for (int r = 0; r < rows; r += stride)
        {
            for (int c = 0; c < cols; c++) _scratch[c] = matrix[r, c];
            ring.Write(_scratch);
        }

        if (AcceptInterval.HasValue) _lastAcceptedUtc = NowMonotonicUtc();
    }

    #endregion

    #region Configure channels (direct set, e.g. *Init)

    /// <summary>UI-thread-safe channel-count set: (re)allocates the ring and rebuilds the surface.</summary>
    protected void ConfigureChannels(int count)
    {
        if (IsDisposed) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ConfigureChannels(count), DispatcherPriority.Render);
            return;
        }
        SetChannelCountInternal(Math.Clamp(count, 0, MaxChannels));
    }

    private void SetChannelCountInternal(int count)
    {
        _numberOfChannels = count;

        if (_numberOfChannels > 0)
        {
            if (_ring == null || _ring.Channels != _numberOfChannels)
                _ring = new SampleRingBuffer(RingCapacityFrames, _numberOfChannels);
            else
                _ring.Clear();
        }
        else
        {
            _ring = null;
        }

        OnRebuild();
    }

    private void RequestChannelReconfigure(int newCount)
    {
        if (IsDisposed) return;
        Volatile.Write(ref _pendingChannelCount, newCount);
        if (Interlocked.Exchange(ref _reconfigureScheduled, 1) == 1) return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _reconfigureScheduled, 0);
            if (IsDisposed) return;
            var count = Volatile.Read(ref _pendingChannelCount);
            SetChannelCountInternal(Math.Clamp(count, 0, MaxChannels));
        }, DispatcherPriority.Render);
    }

    #endregion

    #region Timer flush

    private void RequestFlush()
    {
        if (IsDisposed || !IsActive || IsUpdating || _numberOfChannels == 0) return;
        var ring = _ring;
        if (ring is null || !ring.HasData) return;

        if (Interlocked.Exchange(ref _flushScheduled, 1) == 1) return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            RunFlush();
        }, DispatcherPriority.Render);
    }

    private void RunFlush()
    {
        if (IsDisposed || !IsActive || IsUpdating || _numberOfChannels == 0) return;
        if (!IsRenderReady) return;
        var ring = _ring;
        if (ring is null) return;

        IsUpdating = true;
        try
        {
            int n = ring.BeginRead();
            if (n == 0) return;
            try { OnFlush(ring, n); }
            finally { ring.CommitRead(); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{GetType().Name}] flush error: {ex.Message}");
        }
        finally
        {
            IsUpdating = false;
        }
    }

    #endregion

    #region Lifecycle (IMonitor)

    public virtual void Pause()
    {
        IsActive = false;
        VisualizationTimer.Instance.Unsubscribe(_timerCallback);
        _ring?.Clear();
        OnPaused();
    }

    public virtual void Resume()
    {
        if (IsDisposed) return;
        _ring?.Clear();
        _lastAcceptedUtc = DateTime.MinValue;
        OnResumed();
        IsActive = true;
        VisualizationTimer.Instance.Subscribe(_timerCallback, _tickRate);
    }

    public void ResetData()
    {
        if (IsDisposed) return;
        // Non-blocking: avoids the UI-thread deadlock risk of Dispatcher.Invoke from a worker.
        if (Dispatcher.UIThread.CheckAccess()) ResetCore();
        else Dispatcher.UIThread.Post(ResetCore, DispatcherPriority.Render);
    }

    private void ResetCore()
    {
        _ring?.Clear();
        OnResetData();
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        lock (_mirrorLock) _mirrors = Array.Empty<ChannelRingMonitorBase>();
        Pause();
        OnDispose();
        _ring?.Clear();
        _ring = null;
    }

    #endregion

    #region Monotonic clock

    /// <summary>Strictly-increasing UTC timestamp derived from a high-resolution stopwatch.</summary>
    protected DateTime NowMonotonicUtc()
    {
        var elapsedStamp = Stopwatch.GetTimestamp() - _t0Stamp;
        var elapsedTicks = (long)(elapsedStamp * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency));
        var ticks = _t0UtcTicks + elapsedTicks;

        while (true)
        {
            var prev = Interlocked.Read(ref _lastMonotonicTicks);
            if (ticks <= prev) ticks = prev + 1;
            if (Interlocked.CompareExchange(ref _lastMonotonicTicks, ticks, prev) == prev)
                break;
        }

        return new DateTime(ticks, DateTimeKind.Utc);
    }

    #endregion

    #region Subclass hooks

    /// <summary>Render the <paramref name="frameCount"/> fresh frames in <paramref name="ring"/>
    /// (read via <see cref="SampleRingBuffer.Peek"/>). The base brackets this with BeginRead/CommitRead
    /// and the <see cref="IsUpdating"/> guard — do not call those yourself.</summary>
    protected abstract void OnFlush(SampleRingBuffer ring, int frameCount);

    /// <summary>(Re)build the render surface for the current <see cref="ChannelCount"/>. Runs on the UI
    /// thread after the ring is (re)allocated.</summary>
    protected abstract void OnRebuild();

    /// <summary>Reset display state and rebuild after a data reset. Runs on the UI thread; the ring is
    /// already cleared.</summary>
    protected abstract void OnResetData();

    /// <summary>Reset transient display state on resume (e.g. auto-scale extremes). Optional.</summary>
    protected virtual void OnResumed() { }

    /// <summary>Extra teardown on pause. Optional.</summary>
    protected virtual void OnPaused() { }

    /// <summary>Release surface references on dispose. Optional.</summary>
    protected virtual void OnDispose() { }

    /// <inheritdoc/>
    public abstract void UpdateThemeColors();

    #endregion
}
