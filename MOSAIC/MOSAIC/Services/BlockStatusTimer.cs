using System;
using Avalonia.Threading;
using MOSAIC.Diagnostics;

namespace MOSAIC.Services;

/// <summary>Refreshes all block statuses on the UI thread every 250 ms.</summary>
/// <remarks>Independent of chart refreshes, so suspending plots does not suspend status monitoring.</remarks>
internal sealed class BlockStatusTimer
{
    internal static BlockStatusTimer Instance { get; } = new();

    private readonly object _sync = new();
    private readonly DispatcherTimer _timer;
    private Action[] _callbacks = Array.Empty<Action>();

    private BlockStatusTimer()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTick;
    }

    internal void Subscribe(Action callback)
    {
        lock (_sync)
        {
            if (Array.IndexOf(_callbacks, callback) >= 0) return;
            var updated = new Action[_callbacks.Length + 1];
            Array.Copy(_callbacks, updated, _callbacks.Length);
            updated[^1] = callback;
            _callbacks = updated;
            if (_callbacks.Length == 1) _timer.Start();
        }
    }

    internal void Unsubscribe(Action callback)
    {
        lock (_sync)
        {
            int index = Array.IndexOf(_callbacks, callback);
            if (index < 0) return;
            var updated = new Action[_callbacks.Length - 1];
            Array.Copy(_callbacks, 0, updated, 0, index);
            Array.Copy(_callbacks, index + 1, updated, index, updated.Length - index);
            _callbacks = updated;
            if (_callbacks.Length == 0) _timer.Stop();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Membership changes replace the array; a tick only reads its snapshot.
        // Invoke outside the lock so callbacks may dispose blocks or create new ones.
        Action[] callbacks;
        lock (_sync) callbacks = _callbacks;
        foreach (var callback in callbacks)
        {
            try { callback(); }
            catch (Exception ex)
            {
                Log.Error(nameof(BlockStatusTimer), null, ex, "Block status refresh failed.");
            }
        }
    }
}
