using System;
using System.Diagnostics;
using System.Threading;
using System.Linq;
using MOSAIC.Diagnostics;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using MOSAIC.Components.Interfaces;

namespace MOSAIC.Components.Basics;

/// <summary>
/// Dispatches published values to a set of registered subscribers.
/// </summary>
/// <remarks>
/// <para>
/// Fan-out mechanism: when a publisher calls <see cref="Dispatch(object, object)"/>, the value is
/// queued for every subscriber and delivered by that subscriber's own dedicated consumer pump.
/// </para>
/// <para>
/// <b>Per-subscriber ordering and serialization:</b> each subscriber owns a bounded
/// <see cref="Channel{T}"/> drained by a single long-running pump, so a subscriber observes values in
/// publish order from this dispatcher, with one <see cref="ISubscriber.ReceiveInput"/> call at a time
/// on that subscription. A subscriber connected to several publishers has several independent pumps
/// and must protect shared state against concurrent calls. Different subscribers also run on
/// independent pumps, so branch parallelism is preserved.
/// </para>
/// <para>
/// <b>Non-blocking delivery:</b> <see cref="Dispatch(object, object)"/> only enqueues (a lock-free
/// <c>TryWrite</c> per subscriber) and never blocks the publishing thread, even when a subscriber is
/// slow. Unlike a per-value <c>Task.Run</c> fan-out, there is no per-value task or closure allocation.
/// </para>
/// <para>
/// <b>Backpressure:</b> Each processing subscription has a bounded queue. On overflow,
/// that branch stops accepting input and reports a fault. Already accepted values drain in order.
/// Reconnect the branch after addressing the overload; processing never silently skips a gap.
/// </para>
/// <para>
/// <b>Error handling:</b> an exception thrown by a subscriber is caught and logged through the shared logger; it does not stop that subscriber's pump or affect other subscribers.
/// </para>
/// <para>
/// <b>Thread-safety:</b> the subscriber set is an immutable copy-on-write array guarded by
/// <see cref="_gate"/>. <see cref="AddSubscriber"/>/<see cref="RemoveSubscriber"/> publish a new array
/// under the lock; <see cref="Dispatch(object, object)"/> reads it via a single volatile read with no
/// locking, so adding or removing a subscriber concurrently with a dispatch is safe.
/// </para>
/// <para>
/// <b>Lifetime:</b> <see cref="Dispose"/> completes every channel; each pump drains its backlog and
/// exits. Owners must dispose the dispatcher (e.g. from the publishing block's <c>Dispose</c>) or the
/// pump tasks will stay alive waiting for input.
/// </para>
/// </remarks>
public sealed class OutputDispatcher : IOutputDispatcher, IDisposable, IAsyncDisposable
{
    private sealed class Subscription
    {
        public ISubscriber Subscriber { get; }
        public Channel<(object Sender, object Value)> Mailbox { get; }
        public Task Pump { get; set; } = Task.CompletedTask;

        public int Faulted;
        public volatile bool Retired;

        public Subscription(ISubscriber subscriber, int capacity)
        {
            Subscriber = subscriber;
            Mailbox = Channel.CreateBounded<(object Sender, object Value)>(
                new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait });
        }
    }

    private readonly int _capacity;
    private long _rejected;
    private long _failures;
    public long RejectedValues => Interlocked.Read(ref _rejected);
    public long SubscriberFailures => Interlocked.Read(ref _failures);
    public int QueuedValues => _subs.Sum(sub => sub.Mailbox.Reader.Count);

    /// <summary>Bounds each processing branch; overflow faults the branch until reconnected.</summary>
    public OutputDispatcher(int queueCapacity = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        _capacity = queueCapacity;
    }

    private readonly object _gate = new();
    private volatile Subscription[] _subs = Array.Empty<Subscription>();
    private volatile bool _disposed;
    private readonly List<Task> _pumps = new();
    private Task _completion = Task.CompletedTask;

    /// <summary>
    /// Registers a subscriber so it will receive values dispatched by this instance.
    /// </summary>
    /// <param name="s">The subscriber to add.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="s"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// If the subscriber is already registered, or the dispatcher has been disposed, this has no effect.
    /// </remarks>
    public void AddSubscriber(ISubscriber s)
    {
        ArgumentNullException.ThrowIfNull(s);

        lock (_gate)
        {
            if (_disposed || IndexOf(_subs, s) >= 0)
                return;

            var sub = new Subscription(s, _capacity);
            sub.Pump = RunPump(sub);
            _pumps.RemoveAll(pump => pump.IsCompleted);
            _pumps.Add(sub.Pump);

            var next = new Subscription[_subs.Length + 1];
            Array.Copy(_subs, next, _subs.Length);
            next[^1] = sub;
            _subs = next;
        }
    }

    /// <summary>
    /// Unregisters a subscriber so it no longer receives dispatched values. Any values already queued
    /// for it are still delivered (in order) before its pump exits.
    /// </summary>
    /// <param name="s">The subscriber to remove.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="s"/> is <see langword="null"/>.</exception>
    public void RemoveSubscriber(ISubscriber s)
    {
        ArgumentNullException.ThrowIfNull(s);

        Subscription? removed = null;
        lock (_gate)
        {
            int idx = IndexOf(_subs, s);
            if (idx < 0)
                return;

            removed = _subs[idx];
            removed.Retired = true;
            var next = new Subscription[_subs.Length - 1];
            Array.Copy(_subs, 0, next, 0, idx);
            Array.Copy(_subs, idx + 1, next, idx, _subs.Length - idx - 1);
            _subs = next;
        }

        // Signal the pump to drain its backlog and exit. Done outside the lock.
        removed.Mailbox.Writer.TryComplete();
    }

    /// <summary>
    /// Forwards a published value to all registered subscribers by enqueuing it on each subscriber's
    /// mailbox. Never blocks the publishing thread; delivery happens on the per-subscriber pumps.
    /// </summary>
    /// <param name="sender">The publisher that originated the value.</param>
    /// <param name="value">The value payload to forward.</param>
    public void Dispatch(object sender, object value)
    {
        var subs = _subs;
        foreach (var sub in subs)
        {
            if (sub.Mailbox.Writer.TryWrite((sender, value))) continue;
            if (_disposed || sub.Retired) continue;
            Interlocked.Increment(ref _rejected);
            if (Interlocked.Exchange(ref sub.Faulted, 1) != 0) continue;
            sub.Mailbox.Writer.TryComplete();
            var message = $"Input queue exceeded {_capacity} values. Branch stopped; remove and reconnect it after resolving the overload.";
            if (sub.Subscriber is BaseBlock block) block.ReportError(message);
            else Log.Error("OutputDispatcher", $"{sub.Subscriber.GetType().Name}: {message}");
        }
    }

    /// <summary>
    /// Completes every subscriber mailbox so the pumps drain their backlog and exit. Does not block the
    /// caller waiting for the pumps to finish.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            var subs = _subs;
            _subs = Array.Empty<Subscription>();
            foreach (var sub in subs)
                sub.Mailbox.Writer.TryComplete();
            // Include removed subscriptions whose queued callbacks are still draining.
            _completion = Task.WhenAll(_pumps);
            _pumps.Clear();
        }
    }

    /// <summary>Completes delivery and waits for all active and removed subscriber pumps.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        lock (_gate)
            return new ValueTask(_completion);
    }

    private async Task RunPump(Subscription sub)
    {
        try
        {
            var reader = sub.Mailbox.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // Drain available work in small batches. Never wait for a batch to fill:
                // a lone control sample is delivered immediately, in its original shape.
                for (int n = 0; n < 64 && reader.TryRead(out var item); n++)
                {
                    try { sub.Subscriber.ReceiveInput(item.Sender, item.Value); }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _failures);
                        if (sub.Subscriber is BaseBlock block) block.ReportError("Processing input failed.", ex);
                        else Log.Error("OutputDispatcher", ex, $"Subscriber {sub.Subscriber.GetType().Name} failed.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("OutputDispatcher", ex, "Subscriber pump failed.");
        }
    }

    private static int IndexOf(Subscription[] subs, ISubscriber s)
    {
        for (int i = 0; i < subs.Length; i++)
            if (ReferenceEquals(subs[i].Subscriber, s))
                return i;
        return -1;
    }
}
