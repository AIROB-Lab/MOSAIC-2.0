using System.Collections.Generic;

namespace MOSAIC.Diagnostics;

/// <summary>
/// The bounded tail of the log that the in-app panel reads from.
/// </summary>
/// <remarks>
/// <para>
/// The panel is a tail view, not a transcript: the file is the complete record. So the ring keeps a
/// fixed <see cref="Capacity"/> and simply overwrites, which is what bounds the memory cost of a
/// long lab session no matter how loud a device gets.
/// </para>
/// <para>
/// Any number of threads may touch it. The steady state is two - the sink's single writer thread
/// through <see cref="Add"/>, the UI thread through <see cref="CopyFrom"/> - but a producer calls
/// <see cref="Add"/> itself whenever the sink cannot take the entry: before initialisation, when no
/// log folder could be created, once the sink's writer has given up for good, and once the sink has
/// been shut down.
/// </para>
/// <para>
/// So <see cref="Add"/> is on a data thread's hot path. Every member that touches the shared state -
/// <see cref="Add"/>, <see cref="CopyFrom"/>, <see cref="Clear"/> and <see cref="NextSequence"/> -
/// takes the same lock and holds it for no more than a bounded copy of at most <c>max</c> entries;
/// <see cref="Capacity"/> needs no lock because it never changes after construction. Inside the lock
/// there is no I/O and nothing unbounded, so an EMG thread can never end up waiting on the disk. It
/// is not allocation-free, though: <see cref="CopyFrom"/> appends to the caller's list while holding
/// the lock, so a list that has to grow allocates there. That is amortised and bounded by
/// <c>max</c>, and a caller that cares can size its list once and never pay it again.
/// </para>
/// </remarks>
public sealed class LogRing
{
    /// <summary>Entries retained for the panel. Roughly an hour of ordinary lifecycle logging.</summary>
    public const int DefaultCapacity = 2000;

    private readonly LogEntry[] _buffer;
    private readonly object _gate = new();
    private int _writeIndex;
    private int _retained;
    private long _appended;

    /// <summary>Creates a ring of <see cref="DefaultCapacity"/> entries.</summary>
    public LogRing() : this(DefaultCapacity)
    {
    }

    /// <summary>Creates a ring of <paramref name="capacity"/> entries (at least one).</summary>
    /// <param name="capacity">Number of entries to retain before overwriting the oldest.</param>
    public LogRing(int capacity)
    {
        Capacity = capacity < 1 ? 1 : capacity;
        _buffer = new LogEntry[Capacity];
    }

    /// <summary>Number of entries retained before the oldest is overwritten.</summary>
    public int Capacity { get; }

    /// <summary>
    /// The cursor the next appended entry will carry. A reader that starts here sees only entries
    /// added from now on, which is how a freshly opened panel avoids replaying history it was never
    /// asked for.
    /// </summary>
    /// <remarks>
    /// This is the ring's own append counter, not <see cref="LogEntry.Sequence"/>: producers hand out
    /// sequence numbers before queueing, so two entries can reach the ring in the opposite order to
    /// their ids. A cursor derived from the id would then skip the later arrival or replay it.
    /// </remarks>
    public long NextSequence
    {
        get
        {
            lock (_gate) return _appended;
        }
    }

    /// <summary>
    /// Copies every retained entry appended at or after <paramref name="fromSequence"/> into
    /// <paramref name="into"/>, appending at most <paramref name="max"/>, and returns the cursor to
    /// pass on the next call.
    /// </summary>
    /// <param name="fromSequence">Cursor from the previous call, or 0 for everything retained.</param>
    /// <param name="into">Destination list. Entries are appended; existing content is left alone.</param>
    /// <param name="max">Upper bound on entries copied, so one burst cannot stall the UI thread.</param>
    /// <returns>
    /// The next cursor. Unchanged when nothing was copied. When the caller has fallen further behind
    /// than <see cref="Capacity"/>, the copy silently resumes at the oldest retained entry - the gap
    /// is visible in the file, and stalling the panel to preserve it would help nobody.
    /// </returns>
    public long CopyFrom(long fromSequence, List<LogEntry> into, int max)
    {
        if (into is null || max <= 0) return fromSequence;

        lock (_gate)
        {
            // The common case is the 30 fps tick finding nothing new: answer it from the counter
            // instead of walking the whole retained ring while the writer waits for the lock.
            if (fromSequence >= _appended) return fromSequence;

            var oldest = _appended - _retained;
            var start = fromSequence > oldest ? fromSequence : oldest;
            var available = _appended - start;
            var count = available < max ? (int)available : max;

            // Distance back from the write head, which is at most _retained and so never wraps twice.
            var index = (_writeIndex - (int)(_appended - start) + _buffer.Length) % _buffer.Length;

            for (var i = 0; i < count; i++)
            {
                into.Add(_buffer[index]);
                index = index + 1 == _buffer.Length ? 0 : index + 1;
            }

            return start + count;
        }
    }

    /// <summary>Drops every retained entry. The file is untouched.</summary>
    /// <remarks>
    /// The append counter keeps running, so a reader holding an old cursor simply finds nothing left
    /// to copy rather than being replayed the entries that follow.
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _retained = 0;
        }
    }

    /// <summary>
    /// Appends one entry, overwriting the oldest when full. Called from the sink's writer thread,
    /// and directly from a producer whenever the sink cannot get the entry to the file - it is
    /// absent, its writer has stopped, or its channel has been completed at shutdown.
    /// A merely full queue is not one of those: the channel drops its oldest entry and accepts
    /// the write, so overflow is counted rather than rerouted here.
    /// </summary>
    /// <param name="entry">The entry to retain.</param>
    internal void Add(in LogEntry entry)
    {
        lock (_gate)
        {
            _buffer[_writeIndex] = entry;
            _writeIndex = _writeIndex + 1 == _buffer.Length ? 0 : _writeIndex + 1;
            if (_retained < _buffer.Length) _retained++;
            _appended++;
        }
    }
}
