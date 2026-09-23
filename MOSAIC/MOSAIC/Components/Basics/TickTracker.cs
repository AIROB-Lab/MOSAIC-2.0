using System;
using System.Diagnostics;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Interfaces;

namespace MOSAIC.Components.Basics;

/// <summary>
/// Tracks publish timing to estimate output rate and derive a <see cref="BlockStatus"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TickTracker"/> collects a rolling window of recent tick timestamps (in seconds) and uses them to
/// estimate the current output rate. It is used by processing blocks (e.g., <see cref="BaseBlock"/>) to provide
/// real-time monitoring information such as "Idle", "Lagging", or "Normal".
/// </para>
/// <para>
/// <b>Rate estimation:</b> The current rate is computed from the oldest and newest timestamps in the window as:
/// <c>(n - 1) / (last - first)</c>, where <c>n</c> is the number of recorded ticks currently represented in the buffer.
/// </para>
/// <para>
/// <b>Idle detection:</b> A block is considered idle when the current time exceeds
/// <c>lastTick + 10 / desiredRate</c>. This means the idle timeout scales with the desired rate.
/// </para>
/// <para>
/// <b>Threading:</b> All state access is synchronized via an internal lock, making this type safe to use from
/// multiple threads (e.g., background processing thread + UI timer thread).
/// </para>
/// <para>
/// <b>Timestamps:</b> Ticks are recorded using a monotonic <see cref="Stopwatch"/> and expressed as
/// UNIX-like seconds (seconds since 1970-01-01 UTC), which avoids issues with wall-clock adjustments while still
/// producing globally interpretable values.
/// </para>
/// </remarks>
public class TickTracker : ITickTracker
{
    /// <summary>
    /// Monotonic stopwatch shared across all <see cref="TickTracker"/> instances,
    /// started once at process initialization.
    /// </summary>
    private static readonly Stopwatch sw = Stopwatch.StartNew();

    /// <summary>
    /// Wall-clock anchor captured at startup, expressed as UNIX seconds.
    /// Combined with <see cref="sw"/> to produce monotonic, UNIX-like timestamps.
    /// </summary>
    private static readonly double t0 = DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;

    /// <summary>
    /// Rolling window of recent tick timestamps used for rate estimation.
    /// </summary>
    private readonly CircularBuffer<double> _timestamps = new(100);

    /// <summary>
    /// Lock that synchronizes all access to <see cref="_timestamps"/> and <see cref="LastTick"/>.
    /// </summary>
    private readonly object _sync = new();

    /// <summary>
    /// Gets the timestamp (seconds) of the most recently recorded tick.
    /// </summary>
    /// <remarks>
    /// The value is expressed in UNIX-like seconds as returned by <see cref="Now"/>. The initial value is <c>-1</c>
    /// until the first call to <see cref="RecordTick"/>.
    /// </remarks>
    public double LastTick { get; private set; } = -1;

    /// <summary>
    /// Records a new tick at the current time.
    /// </summary>
    /// <remarks>
    /// The recorded time is obtained from <see cref="Now"/> and stored in a rolling window of recent timestamps.
    /// This method updates <see cref="LastTick"/> and contributes to <see cref="CurrentRate"/> estimation.
    /// </remarks>
    public void RecordTick()
    {
        var now = Now();
        lock (_sync)
        {
            _timestamps.Add(now);
            LastTick = now;
        }
    }

    /// <summary>
    /// Gets the estimated output rate in Hertz (ticks per second) based on recently recorded ticks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rate is computed as <c>(n - 1) / (last - first)</c> using the oldest and newest timestamps currently
    /// represented by the rolling window.
    /// </para>
    /// <para>
    /// Returns <c>0</c> if fewer than two ticks have been recorded, or if the computed time span is not positive.
    /// </para>
    /// </remarks>
    public double CurrentRate
    {
        get
        {
            lock (_sync)
            {
                var n = _timestamps.Count;
                if (n < 2) return 0;

                var dt = _timestamps.Last - _timestamps.First;
                if (dt <= 0) return 0;

                return (n - 1) / dt;
            }
        }
    }

    /// <summary>
    /// Determines whether the tracked stream is considered idle relative to a desired rate.
    /// </summary>
    /// <param name="desiredRate">
    /// Desired rate in Hertz used to compute the inactivity threshold. Must be greater than <c>0</c>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the current time exceeds <c>last + 10 / desiredRate</c>;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The idle threshold scales with <paramref name="desiredRate"/>. For example, at 200 Hz the threshold is 0.05 s;
    /// at 20 Hz it is 0.5 s.
    /// </para>
    /// <para>
    /// This method assumes <paramref name="desiredRate"/> is positive. Passing <c>0</c> (or negative values)
    /// can result in invalid thresholds (division by zero or inverted timeouts).
    /// </para>
    /// </remarks>
    public bool IsIdle(double desiredRate) =>
        Now() > _timestamps.Last + 10 / desiredRate;

    /// <summary>
    /// Determines whether the current estimated rate is lagging relative to a desired rate.
    /// </summary>
    /// <param name="desiredRate">Desired rate in Hertz used as the reference.</param>
    /// <returns>
    /// <see langword="true"/> if <see cref="CurrentRate"/> is positive and less than 95% of
    /// <paramref name="desiredRate"/>; otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// A small tolerance (5%) is applied to avoid oscillating between statuses due to minor jitter.
    /// </remarks>
    public bool IsLagging(double desiredRate) =>
        CurrentRate > 0 && CurrentRate < 0.95 * desiredRate;

    /// <summary>
    /// Computes the current <see cref="BlockStatus"/> based on tick history, desired rate, and stumble state.
    /// </summary>
    /// <param name="desiredRate">
    /// Desired rate in Hertz used to determine idle/lagging thresholds. Should be greater than <c>0</c>.
    /// </param>
    /// <param name="isStumbling">
    /// Indicates whether the caller detected a stumble condition (e.g., contention when attempting to publish).
    /// </param>
    /// <returns>
    /// One of:
    /// <list type="bullet">
    /// <item><description><see cref="BlockStatus.Idle"/> if fewer than two ticks exist or the stream is idle.</description></item>
    /// <item><description><see cref="BlockStatus.Stumbling"/> if <paramref name="isStumbling"/> is <see langword="true"/>.</description></item>
    /// <item><description><see cref="BlockStatus.Lagging"/> if the current rate is below threshold.</description></item>
    /// <item><description><see cref="BlockStatus.Normal"/> otherwise.</description></item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// The method first checks whether any meaningful history exists (at least two ticks). If not, it returns
    /// <see cref="BlockStatus.Idle"/>. Otherwise, it prioritizes <paramref name="isStumbling"/> over rate-based
    /// classifications.
    /// </remarks>
    public BlockStatus GetStatus(double desiredRate, bool isStumbling = false)
    {
        lock (_sync)
        {
            if (_timestamps.Count < 2) return BlockStatus.Idle;
        }

        if (isStumbling)
            return BlockStatus.Stumbling;
        if (IsIdle(desiredRate))
            return BlockStatus.Idle;
        if (IsLagging(desiredRate))
            return BlockStatus.Lagging;

        return BlockStatus.Normal;
    }

    /// <summary>
    /// Returns a monotonic, UNIX-like timestamp in seconds.
    /// </summary>
    /// <returns>
    /// The current time in seconds, represented as an offset from 1970-01-01 UTC, with monotonic progression.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method combines a wall-clock anchor (<c>t0</c>) with a monotonic stopwatch delta to produce timestamps
    /// that are stable against wall-clock corrections (e.g., NTP adjustments) while remaining interpretable as
    /// UNIX-like seconds.
    /// </para>
    /// <para>
    /// The returned value is suitable for interval timing and rate estimation.
    /// </para>
    /// </remarks>
    public static double Now() => t0 + (double)sw.ElapsedTicks / Stopwatch.Frequency;
}