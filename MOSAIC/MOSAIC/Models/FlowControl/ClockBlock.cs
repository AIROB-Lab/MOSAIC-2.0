using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Source block that generates a periodic tick at a configurable rate using a dedicated
/// high-priority background thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> The clock uses <see cref="Stopwatch"/> ticks to schedule publications at
/// approximately <see cref="BaseBlock.DesiredRate"/> Hz. Each tick publishes the current
/// <see cref="TickTracker.Now"/> timestamp (a monotonic <see cref="double"/> in seconds since
/// epoch) to all downstream subscribers.
/// </para>
/// <para>
/// <b>Timing strategy:</b> The loop computes a fixed tick interval
/// (<c>Stopwatch.Frequency / DesiredRate</c>) and uses a catch-up loop to maintain cadence.
/// When ahead of schedule, <see cref="Thread.SpinWait"/> is used instead of <c>Thread.Sleep</c>
/// to avoid OS scheduler granularity (typically 15 ms on Windows), achieving sub-millisecond
/// jitter at rates up to ~2 kHz.
/// </para>
/// <para>
/// <b>Source block:</b> This block does not consume upstream data; <see cref="OnReceive"/>
/// throws <see cref="NotImplementedException"/> if called.
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs each published timestamp value.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "Timer": {
///     "Type": "Clock",
///     "DesiredRate": 200,
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b> None. Rate is configured via <c>DesiredRate</c>.
/// </para>
/// </example>
public partial class ClockBlock : BaseBlock
{
    /// <inheritdoc />
    public override int MinInputs => 0;

    /// <inheritdoc />
    public override int MaxInputs => 0;

    /// <summary>
    /// The worker thread responsible for generating ticks.
    /// </summary>
    private Thread? _thread;
    private readonly object _clockLock = new();
    private bool _clockDisposed;

    /// <summary>
    /// Indicates whether the clock loop is running. Marked <see langword="volatile"/>
    /// to ensure visibility across threads.
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// Gets a value indicating whether the clock is currently running.
    /// </summary>
    public bool Running => _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClockBlock"/> class.
    /// </summary>
    /// <param name="name">Display name of the block.</param>
    /// <param name="desiredRate">Desired tick rate in Hz.</param>
    public ClockBlock(string name = "Clock", double desiredRate = 200)
    {
        Name = name;
        DesiredRate = desiredRate;
    }

    /// <summary>
    /// The clock is the rate source for a synthetic pipeline, so a runtime rate change must reach
    /// already-running consumers. Force the new rate down the whole chain — overriding rates that
    /// downstream blocks latched on a previous run — instead of the default soft propagation, which
    /// a latched downstream rate would otherwise block.
    /// </summary>
    protected override void OnDesiredRateChanged(double oldRate, double newRate)
    {
        base.OnDesiredRateChanged(oldRate, newRate);
        ForcePropagateDesiredRate();
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_ticks.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_ticks";

    /// <summary>
    /// Creates a <see cref="ClockBlock"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">JSON model containing name, desired rate, and optional output path.</param>
    /// <returns>A configured <see cref="ClockBlock"/> instance.</returns>
    public static ClockBlock ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "Clock";
        var rate = m.DesiredRate ?? 200;

        var block = ActivatorUtilities.CreateInstance<ClockBlock>(sp, name, rate);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "Clock";

    #endregion

    /// <summary>
    /// Starts the clock thread if it is not already running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The thread runs with <see cref="ThreadPriority.Highest"/> and uses
    /// <see cref="Stopwatch.ElapsedTicks"/> for scheduling to minimise drift.
    /// A catch-up loop ensures that if the thread falls behind (e.g. due to GC),
    /// missed ticks are published immediately rather than dropped.
    /// </para>
    /// <para>
    /// Between ticks, the loop uses <see cref="Thread.SpinWait"/> (when ahead by a small margin)
    /// or <see cref="Thread.Yield"/> (when exactly on schedule) to avoid the ~15 ms granularity
    /// of <c>Thread.Sleep</c>.
    /// </para>
    /// </remarks>
    public void Start()
    {
        lock (_clockLock)
        {
            ObjectDisposedException.ThrowIf(_clockDisposed, this);
            if (_running) return;
            _running = true;

            _thread = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                long f = Stopwatch.Frequency;
                long dt = Math.Max(1, (long)Math.Round(f / DesiredRate));
                long next = sw.ElapsedTicks;

                while (_running)
                {
                    long now = sw.ElapsedTicks;

                    if (now >= next)
                    {
                        Publish(TickTracker.Now());
                        next += dt;

                        // If we fell more than 1 tick behind, skip ahead instead of bursting
                        if (sw.ElapsedTicks >= next)
                            next = sw.ElapsedTicks + dt;
                    }

                    var left = next - sw.ElapsedTicks;
                    if (left > 0) Thread.SpinWait(64);
                    else Thread.Yield();
                }

                sw.Stop();
            })
            { IsBackground = true, Priority = ThreadPriority.Highest };

            _thread.Start();
        }
    }

    /// <summary>
    /// Stops the clock thread and waits for it to terminate.
    /// </summary>
    public void Stop()
    {
        lock (_clockLock)
        {
            _running = false;
            if (_thread is { } thread && thread != Thread.CurrentThread)
                thread.Join();
            _thread = null;
        }
    }

    public override void Dispose()
    {
        lock (_clockLock)
        {
            _clockDisposed = true;
            Stop();
        }
        base.Dispose();
    }

    /// <summary>
    /// Not implemented — this is a source block that does not consume upstream data.
    /// </summary>
    /// <exception cref="NotImplementedException">Always thrown.</exception>
    protected override void OnReceive(object sender, object value)
    {
        throw new NotImplementedException();
    }
}
