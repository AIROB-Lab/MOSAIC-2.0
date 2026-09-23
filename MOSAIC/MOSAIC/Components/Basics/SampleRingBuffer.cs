using System;
using System.Numerics;
using System.Threading;

namespace MOSAIC.Components.Basics;

/// <summary>
/// Lock-free, allocation-free ring buffer of fixed-width sample frames for the real-time
/// visualization ingest path (one frame = <see cref="Channels"/> doubles).
///
/// <para><b>SPSC:</b> one producer calls <see cref="Write"/>; one consumer calls
/// <see cref="HasData"/>/<see cref="BeginRead"/>/<see cref="Peek"/>/<see cref="CopyFrame"/>/
/// <see cref="CommitRead"/>/<see cref="Clear"/>. Lock-free between the two roles. NOT safe for
/// multiple producers.</para>
///
/// <para><b>Overflow = overwrite-oldest:</b> the producer writes circularly and never blocks, so
/// the ring always holds the most recent <see cref="Capacity"/> frames. If the consumer falls
/// behind, <see cref="BeginRead"/> skips the overwritten-oldest frames (counted in
/// <see cref="DroppedFrames"/>) and returns a batch ENDING at the newest frame — the live signal
/// is never the data dropped. A guard band keeps reads tear-free.</para>
/// </summary>
public sealed class SampleRingBuffer
{
    private readonly double[] _buffer; // frame-major: frame f at [f*_channels, f*_channels + _channels)
    private readonly int _capacity;    // frame count, power of two
    private readonly int _mask;        // _capacity - 1
    private readonly int _channels;
    private readonly int _guard;       // frames withheld from the oldest edge to prevent torn reads

    private long _head;     // total frames written; advanced only by the producer
    private long _tail;     // total frames consumed; advanced only by the consumer
    private long _readHead; // head snapshot frozen by BeginRead; consumer-owned
    private long _dropped;  // diagnostics; consumer-owned

    /// <summary>Number of channels (doubles) per frame.</summary>
    public int Channels => _channels;

    /// <summary>Capacity in frames (rounded up to a power of two at construction).</summary>
    public int Capacity => _capacity;

    /// <summary>Frames held back from the oldest edge of each read to guarantee tear-free reads.</summary>
    public int GuardFrames => _guard;

    /// <summary>Total frames dropped as overwritten-oldest because the consumer fell too far behind.</summary>
    public long DroppedFrames => Volatile.Read(ref _dropped);

    /// <summary>
    /// Creates a ring holding up to <paramref name="capacityFrames"/> frames of
    /// <paramref name="channels"/> doubles. Capacity is rounded up to a power of two. Size it well
    /// above the frames produced between two consumer flushes AND above the largest single batch.
    /// </summary>
    public SampleRingBuffer(int capacityFrames, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        _capacity = (int)BitOperations.RoundUpToPowerOf2((uint)capacityFrames);
        _mask = _capacity - 1;
        _channels = channels;
        _guard = Math.Max(1, _capacity >> 4); // ~6% of capacity

        long cells = (long)_capacity * channels;
        if (cells > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(capacityFrames),
                $"Buffer ({_capacity} frames × {channels} ch) exceeds Array.MaxLength.");

        _buffer = new double[cells];
    }

    /// <summary>Copies one frame in. <b>Producer thread only.</b> Never blocks; values stored verbatim.</summary>
    public void Write(ReadOnlySpan<double> frame)
    {
        if (frame.Length != _channels)
            throw new ArgumentException(
                $"Frame length {frame.Length} does not match channel count {_channels}.", nameof(frame));

        long head = _head; // producer-owned, plain read
        int slot = (int)(head & _mask);
        frame.CopyTo(_buffer.AsSpan(slot * _channels, _channels));
        Volatile.Write(ref _head, head + 1); // publish only after the slot is fully written
    }

    /// <summary>True if at least one fresh frame is available. <b>Consumer thread only.</b></summary>
    public bool HasData => Volatile.Read(ref _head) != _tail;

    /// <summary>
    /// Opens a read batch; returns the number of fresh frames readable at [0, count) via
    /// <see cref="Peek"/>/<see cref="CopyFrame"/>. <b>Consumer thread only.</b> Skips overwritten
    /// oldest frames so the batch ends at the newest. Call <see cref="CommitRead"/> when done.
    /// </summary>
    public int BeginRead()
    {
        long head = Volatile.Read(ref _head);
        long usable = _capacity - _guard;
        long behind = head - _tail;
        if (behind > usable)
        {
            _dropped += behind - usable;
            _tail = head - usable;
        }
        _readHead = head;
        return (int)(_readHead - _tail);
    }

    /// <summary>Reads one channel value of the frame at logical <paramref name="frameIndex"/> (0 = oldest).</summary>
    public double Peek(int frameIndex, int channel)
    {
        int slot = (int)((_tail + frameIndex) & _mask);
        return _buffer[slot * _channels + channel];
    }

    /// <summary>Copies the frame at logical <paramref name="frameIndex"/> (0 = oldest) into <paramref name="dest"/>.</summary>
    public void CopyFrame(int frameIndex, Span<double> dest)
    {
        int slot = (int)((_tail + frameIndex) & _mask);
        _buffer.AsSpan(slot * _channels, _channels).CopyTo(dest);
    }

    /// <summary>Marks the current batch consumed. <b>Consumer thread only.</b></summary>
    public void CommitRead() => Volatile.Write(ref _tail, _readHead);

    /// <summary>Discards all buffered frames. <b>Consumer thread only.</b></summary>
    public void Clear() => Volatile.Write(ref _tail, Volatile.Read(ref _head));
}