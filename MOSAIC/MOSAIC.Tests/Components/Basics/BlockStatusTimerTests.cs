using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;

namespace MOSAIC.Tests.Components.Basics;

[TestClass]
[DoNotParallelize]
public class BlockStatusTimerTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type TimerType = typeof(BaseBlock).Assembly.GetType("MOSAIC.Services.BlockStatusTimer")!;

    // Exercise isolated instances without changing the production singleton or needing wall-clock sleeps.
    private sealed class TimerProbe : IDisposable
    {
        private readonly object _instance = Activator.CreateInstance(TimerType, true)!;
        private readonly List<Action> _registered = new();
        public DispatcherTimer Timer => (DispatcherTimer)TimerType.GetField("_timer", PrivateInstance)!.GetValue(_instance)!;

        public void Subscribe(Action callback)
        {
            TimerType.GetMethod("Subscribe", PrivateInstance)!.Invoke(_instance, new object[] { callback });
            _registered.Add(callback);
        }

        public void Unsubscribe(Action callback) => TimerType.GetMethod("Unsubscribe", PrivateInstance)!
            .Invoke(_instance, new object[] { callback });

        public void Tick() => TickInstance(_instance);

        public void Dispose()
        {
            foreach (var callback in _registered) Unsubscribe(callback);
        }
    }

    private sealed class BlockProbe() : BaseBlock("status", 10)
    {
        protected override void OnReceive(object sender, object value) { }
    }

    private static void TickInstance(object instance) => TimerType.GetMethod("OnTick", PrivateInstance)!
        .Invoke(instance, new object?[] { null, EventArgs.Empty });

    [TestMethod]
    public void OneTimer_RefreshesAllSubscribersAndStopsOnlyAfterLastLeaves()
    {
        using var timer = new TimerProbe();
        int first = 0, second = 0;
        Action a = () => first++;
        Action b = () => second++;
        Assert.IsFalse(timer.Timer.IsEnabled);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), timer.Timer.Interval);
        timer.Subscribe(a);
        timer.Subscribe(a); // duplicate registration must not duplicate updates
        timer.Subscribe(b);
        Assert.IsTrue(timer.Timer.IsEnabled);
        timer.Tick();
        Assert.AreEqual(1, first);
        Assert.AreEqual(1, second);
        timer.Unsubscribe(a);
        Assert.IsTrue(timer.Timer.IsEnabled);
        timer.Tick();
        Assert.AreEqual(1, first);
        Assert.AreEqual(2, second);
        timer.Unsubscribe(b);
        timer.Unsubscribe(b);
        Assert.IsFalse(timer.Timer.IsEnabled);
        timer.Subscribe(a);
        Assert.IsTrue(timer.Timer.IsEnabled);
        timer.Tick();
        Assert.AreEqual(2, first);
    }

    [TestMethod]
    public void FailingRefresh_DoesNotPreventOtherBlocksRefreshing()
    {
        using var timer = new TimerProbe();
        int refreshed = 0;
        timer.Subscribe(() => throw new InvalidOperationException("Test refresh failure"));
        timer.Subscribe(() => refreshed++);
        timer.Tick();
        timer.Tick();
        Assert.AreEqual(2, refreshed);
    }

    [TestMethod]
    public void MembershipChangesDuringRefresh_TakeEffectOnNextTick()
    {
        using var timer = new TimerProbe();
        int original = 0, replacement = 0;
        Action next = () => replacement++;
        Action swap = null!;
        swap = () =>
        {
            original++;
            timer.Unsubscribe(swap);
            timer.Subscribe(next);
        };
        timer.Subscribe(swap);
        timer.Tick();
        Assert.AreEqual(1, original);
        Assert.AreEqual(0, replacement);
        timer.Tick();
        Assert.AreEqual(1, original);
        Assert.AreEqual(1, replacement);
    }

    [TestMethod]
    public void BlocksShareRefresh_DetectSilenceWithoutPublishingAndUnregisterOnDisposal()
    {
        var shared = TimerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        using var first = new BlockProbe();
        using var second = new BlockProbe();
        foreach (var block in new[] { first, second })
        {
            var tracker = (TickTracker)typeof(BaseBlock).GetField("_tickTracker", PrivateInstance)!.GetValue(block)!;
            var history = (CircularBuffer<double>)typeof(TickTracker).GetField("_timestamps", PrivateInstance)!.GetValue(tracker)!;
            double now = TickTracker.Now();
            history.Add(now - 3);
            history.Add(now - 2);
            block.Status = BlockStatus.Normal;
        }
        TickInstance(shared);
        Assert.AreEqual(BlockStatus.Idle, first.Status);
        Assert.AreEqual(BlockStatus.Idle, second.Status);
        first.Dispose();
        var callbacks = (Action[])TimerType.GetField("_callbacks", PrivateInstance)!.GetValue(shared)!;
        Assert.IsFalse(callbacks.Any(callback => ReferenceEquals(callback.Target, first)));
        Assert.IsTrue(callbacks.Any(callback => ReferenceEquals(callback.Target, second)));
        second.Status = BlockStatus.Normal;
        TickInstance(shared);
        Assert.AreEqual(BlockStatus.Idle, second.Status);
    }
}
