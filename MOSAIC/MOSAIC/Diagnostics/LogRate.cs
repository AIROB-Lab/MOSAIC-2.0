using System;

namespace MOSAIC.Diagnostics;

/// <summary>
/// Per-call-site throttle, so a failure that recurs once per packet is reported once per interval.
/// </summary>
/// <remarks>
/// <para>
/// Hold one as a private field beside the call site it guards:
/// </para>
/// <code>
/// private LogRate _warnRate = new(TimeSpan.FromSeconds(2));
/// ...
/// if (_warnRate.Allow())
///     Log.Warn("OnlineICA", Name, $"Deflation stalled ({_warnRate.Suppressed} suppressed)");
/// </code>
/// <para>
/// Deliberately unsynchronised. It is called from data threads, and a lock there would cost more
/// than the problem it solves; under a race the worst outcome is one duplicated line or one
/// slightly wrong suppressed count, neither of which matters to a reader.
/// </para>
/// <para>
/// A <see langword="default"/> instance has no interval and therefore allows every call. Always
/// construct one with <see cref="LogRate(TimeSpan)"/>.
/// </para>
/// </remarks>
public struct LogRate
{
    private readonly long _intervalMs;
    private long _nextTicks;
    private int _pending;
    private int _suppressed;

    /// <summary>Creates a throttle that allows at most one call per <paramref name="minInterval"/>.</summary>
    /// <param name="minInterval">Minimum gap between two allowed calls.</param>
    public LogRate(TimeSpan minInterval)
    {
        var ms = (long)minInterval.TotalMilliseconds;
        _intervalMs = ms < 0 ? 0 : ms;
        _nextTicks = 0;
        _pending = 0;
        _suppressed = 0;
    }

    /// <summary>
    /// True at most once per interval, and always true the first time. Never blocks and never
    /// allocates, so it is safe on a data thread.
    /// </summary>
    /// <returns>True when the caller should log.</returns>
    public bool Allow()
    {
        var now = Environment.TickCount64;
        if (now < _nextTicks)
        {
            _pending++;
            return false;
        }

        _nextTicks = now + _intervalMs;
        _suppressed = _pending;
        _pending = 0;
        return true;
    }

    /// <summary>
    /// How many calls were suppressed since the previous allowed one. Read it right after
    /// <see cref="Allow"/> returns true, so the line that does get logged says what it stands for.
    /// </summary>
    public int Suppressed => _suppressed;
}
