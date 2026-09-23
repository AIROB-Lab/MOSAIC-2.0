using System;
using System.Threading;

namespace MOSAIC.Components.Basics;

/// <summary>
/// Lock-free, allocation-free "latest value wins" handoff for one multi-channel frame, for the
/// latest-only monitors (spider, heatmap) that render only the newest sample each flush.
///
/// <para><b>Why:</b> these monitors push a frame per sample but the flush keeps only the last one,
/// so a queue is pure waste. This slot keeps exactly the newest frame in pre-allocated buffers —
/// the producer overwrites, the consumer reads the latest. Steady-state allocates nothing.</para>
///
/// <para><b>SPSC triple buffer:</b> one producer calls <see cref="Write"/>; one consumer calls
/// <see cref="TryReadInto"/>/<see cref="HasData"/>/<see cref="Clear"/>. Three buffers — the producer
/// always owns one (back), the consumer always owns one (front), and a shared "ready" slot (middle)
/// is swapped via a single atomic. The producer and consumer never touch the same buffer, so reads
/// are tear-free and wait-free; intermediate frames are simply skipped. NOT safe for multiple producers.</para>
/// </summary>
public sealed class LatestFrameSlot
{
    /// <summary>The three reusable frame buffers.</summary>
    private readonly double[][] _buffers;
    private readonly int _channels;

    /// <summary>
    /// Packed indices in one atomic int:
    /// bits 0-1 = front (consumer's), bits 2-3 = middle (ready/spare),
    /// bits 4-5 = back (producer's), bit 6 (0x40) = dirty (new frame ready).
    /// </summary>
    private int _flags;
    private int _writeIndex; // producer-owned cache == back
    private int _readIndex;  // consumer-owned cache == front

    /// <summary>Channels (doubles) per frame.</summary>
    public int Channels => _channels;

    public LatestFrameSlot(int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        _channels = channels;
        _buffers = new double[3][];
        for (int i = 0; i < 3; i++) _buffers[i] = new double[channels];

        // front=0, middle=1, back=2, dirty=0
        _flags = (2 << 4) | (1 << 2) | 0;
        _writeIndex = 2;
        _readIndex = 0;
    }

    /// <summary>True if a frame newer than the last read is waiting. <b>Consumer thread only.</b></summary>
    public bool HasData => (Volatile.Read(ref _flags) & 0x40) != 0;

    /// <summary>Writes one frame, becoming the new "latest". <b>Producer thread only.</b></summary>
    public void Write(ReadOnlySpan<double> frame)
    {
        if (frame.Length != _channels)
            throw new ArgumentException(
                $"Frame length {frame.Length} does not match channel count {_channels}.", nameof(frame));

        frame.CopyTo(_buffers[_writeIndex]);

        // Publish: swap back <-> middle, set dirty, keep front.
        int old, neu;
        do
        {
            old = Volatile.Read(ref _flags);
            neu = 0x40 | ((old & 0x30) >> 2) | ((old & 0x0C) << 2) | (old & 0x03);
        }
        while (Interlocked.CompareExchange(ref _flags, neu, old) != old);

        _writeIndex = (old & 0x0C) >> 2; // new back = old middle
    }

    /// <summary>
    /// Copies the latest frame into <paramref name="dest"/> (≥ <see cref="Channels"/> long) and
    /// returns true, or returns false if nothing new since the last read. <b>Consumer thread only.</b>
    /// </summary>
    public bool TryReadInto(Span<double> dest)
    {
        // Acquire: swap front <-> middle, clear dirty, keep back.
        int old, neu;
        do
        {
            old = Volatile.Read(ref _flags);
            if ((old & 0x40) == 0) return false; // nothing new
            neu = (old & 0x30) | ((old & 0x03) << 2) | ((old & 0x0C) >> 2);
        }
        while (Interlocked.CompareExchange(ref _flags, neu, old) != old);

        _readIndex = (old & 0x0C) >> 2; // new front = old middle (the latest published)
        _buffers[_readIndex].AsSpan(0, _channels).CopyTo(dest);
        return true;
    }

    /// <summary>Discards the pending frame so the next <see cref="TryReadInto"/> returns false until
    /// a new <see cref="Write"/> arrives. <b>Consumer thread only</b> (e.g. on pause/reset).</summary>
    public void Clear()
    {
        int old, neu;
        do
        {
            old = Volatile.Read(ref _flags);
            neu = old & ~0x40; // clear dirty only
        }
        while (Interlocked.CompareExchange(ref _flags, neu, old) != old);
    }
}