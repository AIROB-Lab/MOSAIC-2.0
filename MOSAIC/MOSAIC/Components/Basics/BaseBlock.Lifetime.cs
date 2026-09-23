using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MOSAIC.Components.Basics;

public abstract partial class BaseBlock
{
    private readonly object _lifetimeLock = new();
    private readonly Dictionary<Type, object> _ownedResources = new();
    private volatile bool _stopping;
    private int _activeReceivers;
    private TaskCompletionSource? _receiversStopped;
    private Task? _disposeTask;
    private readonly List<Task> _cleanupTasks = new();
    internal event EventHandler? Disposing;

    /// <summary>Includes a device disconnect or extra recording close in asynchronous disposal.</summary>
    protected void TrackCleanup(Task cleanup)
    {
        lock (_lifetimeLock)
        {
            _cleanupTasks.RemoveAll(task => task.IsCompletedSuccessfully);
            _cleanupTasks.Add(cleanup);
        }
    }

    /// <summary>
    /// Returns one shared resource per type for this block and disposes it with the block.
    /// Card templates use this to retain the same ViewModel across grouping and pop-out views.
    /// </summary>
    internal T GetOrCreateOwned<T>(Func<T> create) where T : class
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_ownedResources.TryGetValue(typeof(T), out var existing))
                return (T)existing;
            var resource = create();
            _ownedResources.Add(typeof(T), resource);
            return resource;
        }
    }

    /// <summary>
    /// Rejects further input/publication and waits for callbacks already executing and output pumps.
    /// Queued inputs to stopping blocks are discarded. This is teardown, not a lossless graph flush.
    /// </summary>
    internal Task StopProcessingAsync()
    {
        Task receivers;
        lock (_lifetimeLock)
        {
            _stopping = true;
            receivers = _activeReceivers == 0
                ? Task.CompletedTask
                : (_receiversStopped ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        lock (_sendLock)
            (_dispatcher as IDisposable)?.Dispose();
        var delivery = _dispatcher is IAsyncDisposable dispatcher
            ? dispatcher.DisposeAsync().AsTask()
            : Task.CompletedTask;
        return Task.WhenAll(receivers, delivery);
    }

    /// <summary>Stops processing, releases model/card resources, and waits for recording closure.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_lifetimeLock)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = CompleteDisposalAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try { await DisposeBlockAsync(); completion.TrySetResult(); }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    private async Task DisposeBlockAsync()
    {
        await StopProcessingAsync();
        try
        {
            // Keep device and UI-owned resource disposal on the caller's context.
            if (!_disposed) Dispose();
        }
        finally
        {
            ReleaseBaseResources();
            Task[] cleanup;
            lock (_lifetimeLock) cleanup = _cleanupTasks.ToArray();
            await Task.WhenAll(cleanup);
        }
    }
}
