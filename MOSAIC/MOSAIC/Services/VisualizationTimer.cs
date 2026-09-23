using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace MOSAIC.Services;

/// <summary>
/// Shared timer service for all visualization components.
/// Supports multiple tick rates (30fps, 60fps) with automatic start/stop.
/// </summary>
public sealed class VisualizationTimer : IDisposable
{
    private static VisualizationTimer? _instance;
    private static readonly object _instanceLock = new();

    public static VisualizationTimer Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_instanceLock)
                {
                    _instance ??= new VisualizationTimer();
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// Standard tick rates supported by the timer.
    /// </summary>
    public enum TickRate
    {
        Fps30 = 30,
        Fps60 = 60
    }

    private class TimerGroup
    {
        public DispatcherTimer Timer { get; }
        public List<Action> Subscribers { get; } = new();
        public int FrameCounter { get; set; }

        public TimerGroup(int intervalMs)
        {
            Timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(intervalMs)
            };
        }
    }

    // Base timer runs at 60fps, 30fps subscribers get called every other frame
    private readonly DispatcherTimer _masterTimer;
    private readonly Dictionary<TickRate, List<Action>> _subscribers = new();
    private readonly object _subscriberLock = new();
    private int _frameCounter;

    // Suspension (e.g. during a window/pane resize drag) — re-entrant via a counter.
    private int _suspendCount;

    private const int MasterIntervalMs = 1000 / 60; // 60fps base

    private VisualizationTimer()
    {
        _subscribers[TickRate.Fps30] = new List<Action>();
        _subscribers[TickRate.Fps60] = new List<Action>();

        _masterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(MasterIntervalMs)
        };
        _masterTimer.Tick += OnMasterTick;
    }

    private void OnMasterTick(object? sender, EventArgs e)
    {
        _frameCounter++;

        List<Action> fps60Subscribers;
        List<Action> fps30Subscribers;

        lock (_subscriberLock)
        {
            fps60Subscribers = new List<Action>(_subscribers[TickRate.Fps60]);
            fps30Subscribers = _frameCounter % 2 == 0
                ? new List<Action>(_subscribers[TickRate.Fps30])
                : new List<Action>();
        }

        // Call 60fps subscribers every frame
        foreach (var callback in fps60Subscribers)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VisualizationTimer] 60fps subscriber error: {ex.Message}");
            }
        }

        // Call 30fps subscribers every other frame
        foreach (var callback in fps30Subscribers)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VisualizationTimer] 30fps subscriber error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Subscribe to timer ticks at the default rate (30fps).
    /// </summary>
    public void Subscribe(Action callback) => Subscribe(callback, TickRate.Fps30);

    /// <summary>
    /// Subscribe to timer ticks at a specific rate.
    /// </summary>
    public void Subscribe(Action callback, TickRate rate)
    {
        if (callback == null) return;

        lock (_subscriberLock)
        {
            var list = _subscribers[rate];
            if (!list.Contains(callback))
            {
                list.Add(callback);

                // Start the timer when the first subscriber joins — unless ticks are suspended.
                if (TotalSubscriberCount == 1 && _suspendCount == 0)
                {
                    _masterTimer.Start();
                    Console.WriteLine("[VisualizationTimer] Started (first subscriber)");
                }
            }
        }
    }

    /// <summary>
    /// Unsubscribe from timer ticks.
    /// </summary>
    public void Unsubscribe(Action callback)
    {
        if (callback == null) return;

        lock (_subscriberLock)
        {
            foreach (var list in _subscribers.Values)
            {
                list.Remove(callback);
            }

            // Stop timer when last subscriber leaves
            if (TotalSubscriberCount == 0)
            {
                _masterTimer.Stop();
                Console.WriteLine("[VisualizationTimer] Stopped (no subscribers)");
            }
        }
    }

    /// <summary>
    /// Temporarily stops all visualization ticks (e.g. while a window/pane is being resized) so the
    /// UI thread isn't doing live chart work mid-drag. Re-entrant: balanced with <see cref="ResumeTicks"/>.
    /// </summary>
    public void SuspendTicks()
    {
        lock (_subscriberLock)
        {
            if (++_suspendCount == 1)
                _masterTimer.Stop();
        }
    }

    /// <summary>
    /// Releases a <see cref="SuspendTicks"/>. Once the last suspension is released the timer resumes
    /// if there are still subscribers.
    /// </summary>
    public void ResumeTicks()
    {
        lock (_subscriberLock)
        {
            if (_suspendCount == 0) return;
            if (--_suspendCount == 0 && TotalSubscriberCount > 0)
                _masterTimer.Start();
        }
    }

    private int TotalSubscriberCount
    {
        get
        {
            int count = 0;
            foreach (var list in _subscribers.Values)
                count += list.Count;
            return count;
        }
    }

    public int SubscriberCount(TickRate rate)
    {
        lock (_subscriberLock)
        {
            return _subscribers[rate].Count;
        }
    }

    public void Dispose()
    {
        _masterTimer.Stop();
        lock (_subscriberLock)
        {
            foreach (var list in _subscribers.Values)
                list.Clear();
        }
    }
}