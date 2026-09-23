using System;
using System.Threading;
using System.Threading.Tasks;

namespace MOSAIC.Components.Manager.BLE;

/// <summary>
/// Serializes callback-based writes across one GATT connection, not just one characteristic.
/// A failed/cancelled write invalidates the queue: its late callback must never complete a retry.
/// </summary>
internal sealed class GattOperationQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly TimeSpan _timeout;
    private (string Key, TaskCompletionSource Completion)? _pending;
    private Exception? _failure;

    internal GattOperationQueue(TimeSpan? timeout = null)
        => _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public async Task ExecuteAsync(string key, Func<bool> start, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                if (_failure is not null)
                    throw new InvalidOperationException("The Bluetooth connection must be reconnected after a failed GATT operation.", _failure);
                ct.ThrowIfCancellationRequested();
                _pending = (key, completion);
                if (!start())
                    throw new InvalidOperationException($"Android rejected the Bluetooth write ({key}).");
            }

            await completion.Task.WaitAsync(_timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_sync)
                _failure ??= ex;
            throw;
        }
        finally
        {
            lock (_sync)
                _pending = null;
            _gate.Release();
        }
    }

    public void Complete(string key, int status)
    {
        lock (_sync)
        {
            if (_pending is not { } pending || pending.Key != key) return;
            if (status == 0)
                pending.Completion.TrySetResult();
            else
                pending.Completion.TrySetException(new InvalidOperationException(
                    $"Bluetooth write failed ({key}, GATT status {status})."));
        }
    }

    public void Fail(Exception error)
    {
        lock (_sync)
        {
            _failure ??= error;
            _pending?.Completion.TrySetException(error);
        }
    }
}
