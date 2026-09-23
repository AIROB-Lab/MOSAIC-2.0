using System;
using System.Diagnostics;
using System.Threading;

namespace MOSAIC.Components.Devices.Delsys;

/// <summary>
/// High-resolution periodic timer using <see cref="Stopwatch"/> busy-waiting.
/// Suitable for 1–4 kHz sample dispatch where OS timers lack precision.
/// </summary>
public sealed class HighResTimer : IDisposable
{
    private readonly long   _ticksPerInterval;
    private readonly Action _callback;
    private readonly Thread _thread;
    private volatile bool   _running;

    /// <param name="frequencyHz">Target frequency in Hz (e.g. 4000).</param>
    /// <param name="callback">Action invoked on every tick.</param>
    public HighResTimer(int frequencyHz, Action callback)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequencyHz);
        ArgumentNullException.ThrowIfNull(callback);

        if (!Stopwatch.IsHighResolution)
            throw new InvalidOperationException("High-resolution performance counter not available.");

        _ticksPerInterval = Stopwatch.Frequency / frequencyHz;
        _callback         = callback;
        _thread           = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest };
    }

    /// <summary>Starts the timer thread. No-op if already running.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread.Start();
    }

    /// <summary>Signals the timer loop to exit.</summary>
    public void Stop() => _running = false;

    private void Run()
    {
        var sw = Stopwatch.StartNew();
        long next = sw.ElapsedTicks;

        while (_running)
        {
            next += _ticksPerInterval;

            while (sw.ElapsedTicks < next)
                Thread.SpinWait(10);

            _callback();
        }

        Console.WriteLine("[HighResTimer] Run() exited");
    }

    public void Dispose() => Stop();
}