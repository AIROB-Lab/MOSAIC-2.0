using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MOSAIC.Tests.Components.Basics;

/// <summary>
/// Known-answer tests for <see cref="TickTracker"/>.
///
/// Rate estimation: CurrentRate = (n - 1) / (last - first), where n is the number of ticks
/// currently in the rolling window; it returns 0 when n &lt; 2 or the span is not positive.
///
/// Status: GetStatus returns Idle when fewer than two ticks exist (before any rate/idle math),
/// Stumbling when isStumbling is true (checked before rate-based classification), and otherwise
/// derives Idle/Lagging/Normal from the rate.
///
/// Because Now() reads a real monotonic clock, only the deterministic branches below are asserted;
/// no wall-clock-derived numeric rates are checked.
/// </summary>
[TestClass]
public class TickTrackerTests
{
    [TestMethod]
    public void LastTick_BeforeAnyTick_IsMinusOne()
    {
        // Documented initial sentinel value before the first RecordTick.
        var tracker = new TickTracker();

        Assert.AreEqual(-1.0, tracker.LastTick, 1e-9);
    }

    [TestMethod]
    public void CurrentRate_NoTicks_IsZero()
    {
        // n = 0 < 2  =>  rate short-circuits to 0.
        var tracker = new TickTracker();

        Assert.AreEqual(0.0, tracker.CurrentRate, 1e-9);
    }

    [TestMethod]
    public void CurrentRate_OneTick_IsZero()
    {
        // n = 1 < 2  =>  rate short-circuits to 0 (need at least two timestamps to span an interval).
        var tracker = new TickTracker();

        tracker.RecordTick();

        Assert.AreEqual(0.0, tracker.CurrentRate, 1e-9);
    }

    [TestMethod]
    public void RecordTick_UpdatesLastTickAwayFromSentinel()
    {
        // After a tick, LastTick is set to Now() (a positive UNIX-like second count),
        // so it is no longer the -1 sentinel. Now() = t0 + elapsed >= t0 > 0.
        var tracker = new TickTracker();

        tracker.RecordTick();

        Assert.IsTrue(tracker.LastTick > 0.0);
    }

    [TestMethod]
    public void GetStatus_NoTicks_ReturnsIdle()
    {
        // _timestamps.Count = 0 < 2  =>  Idle, before any rate/idle-threshold evaluation.
        var tracker = new TickTracker();

        var status = tracker.GetStatus(desiredRate: 100.0);

        Assert.AreEqual(BlockStatus.Idle, status);
    }

    [TestMethod]
    public void GetStatus_OneTick_ReturnsIdle()
    {
        // _timestamps.Count = 1 < 2  =>  Idle regardless of desiredRate.
        var tracker = new TickTracker();

        tracker.RecordTick();
        var status = tracker.GetStatus(desiredRate: 100.0);

        Assert.AreEqual(BlockStatus.Idle, status);
    }

    [TestMethod]
    public void GetStatus_FewerThanTwoTicks_IdleWinsOverStumbling()
    {
        // The count guard (< 2 => Idle) is evaluated before the isStumbling check,
        // so with a single tick the result is Idle even when isStumbling is true.
        var tracker = new TickTracker();

        tracker.RecordTick();
        var status = tracker.GetStatus(desiredRate: 100.0, isStumbling: true);

        Assert.AreEqual(BlockStatus.Idle, status);
    }

    [TestMethod]
    public void GetStatus_TwoTicksAndStumbling_ReturnsStumbling()
    {
        // With two ticks the count guard passes; isStumbling is then prioritized over
        // any rate-based (idle/lagging) classification  =>  Stumbling.
        var tracker = new TickTracker();

        tracker.RecordTick();
        tracker.RecordTick();
        var status = tracker.GetStatus(desiredRate: 100.0, isStumbling: true);

        Assert.AreEqual(BlockStatus.Stumbling, status);
    }

    [TestMethod]
    public void IsLagging_NoTicks_IsFalse()
    {
        // CurrentRate = 0 (n < 2), and IsLagging requires CurrentRate > 0  =>  false.
        var tracker = new TickTracker();

        Assert.IsFalse(tracker.IsLagging(desiredRate: 100.0));
    }

    [TestMethod]
    public void IsLagging_OneTick_IsFalse()
    {
        // Still only one timestamp => CurrentRate = 0 => not lagging.
        var tracker = new TickTracker();

        tracker.RecordTick();

        Assert.IsFalse(tracker.IsLagging(desiredRate: 100.0));
    }
}
