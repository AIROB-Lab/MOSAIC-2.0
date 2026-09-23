using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Tests.TAC;

namespace MOSAIC.Tests.Components.Tests.TAC;

/// <summary>
/// Known-answer tests for <see cref="DwellDetector"/>, a deterministic state machine driven by an
/// injected clock: <c>Update(distance, now)</c>.
///
/// State rules (from the definition):
///  - If distance ≤ Threshold: on first entry, _entryTime = now. Then HoldTime = now − _entryTime,
///    HoldProgress = min(1, HoldTime / DwellTime). IsComplete becomes true once HoldTime ≥ DwellTime.
///  - If distance > Threshold: if it was previously in-target, Overshoots++. Timer resets
///    (InTarget=false, HoldTime=0, HoldProgress=0, _entryTime=now).
///  - Once IsComplete, Update is a no-op.
///  - Reset clears every field back to the fresh state.
/// </summary>
[TestClass]
public class DwellDetectorTests
{
    [TestMethod]
    public void Ctor_StoresDwellTimeAndThreshold_AndStartsIdle()
    {
        // Constructor records dwellTime + threshold; all mutable state starts at its zero value.
        var detector = new DwellDetector(dwellTime: 1.0, threshold: 0.5);

        Assert.AreEqual(1.0, detector.DwellTime, 1e-9);
        Assert.AreEqual(0.5, detector.Threshold, 1e-9);
        Assert.IsFalse(detector.InTarget);
        Assert.AreEqual(0.0, detector.HoldTime, 1e-9);
        Assert.AreEqual(0.0, detector.HoldProgress, 1e-9);
        Assert.AreEqual(0, detector.Overshoots);
        Assert.IsFalse(detector.IsComplete);
    }

    [TestMethod]
    public void Update_WithinThresholdAccumulates_HoldTimeAndProgressGrow()
    {
        // dwellTime=1.0, threshold=0.5. Enter target at t=0 (distance 0.2 ≤ 0.5) => _entryTime=0.
        // At t=0: HoldTime = 0 − 0 = 0; HoldProgress = min(1, 0/1) = 0.
        // At t=0.5: HoldTime = 0.5 − 0 = 0.5; HoldProgress = min(1, 0.5/1) = 0.5; not complete.
        var detector = new DwellDetector(dwellTime: 1.0, threshold: 0.5);

        detector.Update(distance: 0.2, now: 0.0);
        Assert.IsTrue(detector.InTarget);
        Assert.AreEqual(0.0, detector.HoldTime, 1e-9);     // 0 − 0
        Assert.AreEqual(0.0, detector.HoldProgress, 1e-9); // min(1, 0/1)
        Assert.IsFalse(detector.IsComplete);

        detector.Update(distance: 0.3, now: 0.5);
        Assert.AreEqual(0.5, detector.HoldTime, 1e-9);     // 0.5 − 0
        Assert.AreEqual(0.5, detector.HoldProgress, 1e-9); // min(1, 0.5/1)
        Assert.IsFalse(detector.IsComplete);
        Assert.AreEqual(0, detector.Overshoots);
    }

    [TestMethod]
    public void Update_HoldTimeReachesDwellTime_IsCompleteFlipsTrueAndProgressClamps()
    {
        // dwellTime=1.0, threshold=0.5. Entry at t=2.0 (distance 0.1) => _entryTime=2.0.
        // At t=3.0: HoldTime = 3.0 − 2.0 = 1.0 ≥ dwellTime => IsComplete = true.
        // HoldProgress = min(1, 1.0/1.0) = 1.0.
        var detector = new DwellDetector(dwellTime: 1.0, threshold: 0.5);

        detector.Update(distance: 0.1, now: 2.0);
        detector.Update(distance: 0.1, now: 3.0);

        Assert.IsTrue(detector.IsComplete);
        Assert.AreEqual(1.0, detector.HoldTime, 1e-9);     // 3.0 − 2.0
        Assert.AreEqual(1.0, detector.HoldProgress, 1e-9); // min(1, 1.0/1.0), clamped
    }

    [TestMethod]
    public void Update_HoldProgressClampsToOne_WhenHoldExceedsDwellTime()
    {
        // dwellTime=0.5, threshold=1.0. Entry at t=0 (distance 0.5) => _entryTime=0.
        // At t=2.0: HoldTime = 2.0 ≥ 0.5 => complete. Raw progress = 2.0/0.5 = 4.0, clamped to 1.0.
        var detector = new DwellDetector(dwellTime: 0.5, threshold: 1.0);

        detector.Update(distance: 0.5, now: 0.0);
        detector.Update(distance: 0.5, now: 2.0);

        Assert.IsTrue(detector.IsComplete);
        Assert.AreEqual(2.0, detector.HoldTime, 1e-9);     // 2.0 − 0
        Assert.AreEqual(1.0, detector.HoldProgress, 1e-9); // min(1, 4.0) => 1.0
    }

    [TestMethod]
    public void Update_LeavingTargetAfterEntry_IncrementsOvershootsAndResetsTimer()
    {
        // dwellTime=1.0, threshold=0.5. Enter at t=0 (0.2), accumulate to t=0.4 (HoldTime=0.4).
        // Then distance 0.9 > 0.5 at t=0.5: was in target => Overshoots=1, timer resets to 0.
        var detector = new DwellDetector(dwellTime: 1.0, threshold: 0.5);

        detector.Update(distance: 0.2, now: 0.0);
        detector.Update(distance: 0.2, now: 0.4);
        Assert.AreEqual(0.4, detector.HoldTime, 1e-9); // 0.4 − 0

        detector.Update(distance: 0.9, now: 0.5);

        Assert.AreEqual(1, detector.Overshoots);
        Assert.IsFalse(detector.InTarget);
        Assert.AreEqual(0.0, detector.HoldTime, 1e-9);     // reset on leaving
        Assert.AreEqual(0.0, detector.HoldProgress, 1e-9); // reset on leaving
        Assert.IsFalse(detector.IsComplete);
    }

    [TestMethod]
    public void Update_OutOfTargetWithoutPriorEntry_DoesNotCountOvershoot()
    {
        // First sample is already out of target (0.9 > 0.5). wasInTarget=false at entry,
        // so no overshoot is counted; state simply stays idle.
        var detector = new DwellDetector(dwellTime: 1.0, threshold: 0.5);

        detector.Update(distance: 0.9, now: 0.0);

        Assert.AreEqual(0, detector.Overshoots);
        Assert.IsFalse(detector.InTarget);
        Assert.AreEqual(0.0, detector.HoldTime, 1e-9);
        Assert.IsFalse(detector.IsComplete);
    }

    [TestMethod]
    public void Update_ReEntryAfterOvershoot_RestartsTimerFromNewEntryTime()
    {
        // dwellTime=1.0, threshold=0.5.
        // t=0.0 enter (0.2). t=0.5 leave (0.9) => Overshoots=1, timer reset, _entryTime=0.5.
        // t=1.0 re-enter (0.2): _entryTime becomes 1.0 (fresh entry) => HoldTime = 1.0 − 1.0 = 0.
        // t=1.7 hold (0.2): HoldTime = 1.7 − 1.0 = 0.7; not complete (< 1.0).
        var detector = new DwellDetector(dwellTime: 1.0, threshold: 0.5);

        detector.Update(distance: 0.2, now: 0.0);
        detector.Update(distance: 0.9, now: 0.5);
        detector.Update(distance: 0.2, now: 1.0);
        Assert.AreEqual(0.0, detector.HoldTime, 1e-9); // 1.0 − 1.0, timer restarted at re-entry

        detector.Update(distance: 0.2, now: 1.7);

        Assert.AreEqual(1, detector.Overshoots);
        Assert.IsTrue(detector.InTarget);
        Assert.AreEqual(0.7, detector.HoldTime, 1e-9);           // 1.7 − 1.0
        Assert.AreEqual(0.7, detector.HoldProgress, 1e-9);       // min(1, 0.7/1.0)
        Assert.IsFalse(detector.IsComplete);
    }

    [TestMethod]
    public void Update_DistanceExactlyAtThreshold_CountsAsInTarget()
    {
        // Guard boundary: the condition is distance ≤ Threshold, so distance == Threshold is in-target.
        var detector = new DwellDetector(dwellTime: 1.0, threshold: 0.5);

        detector.Update(distance: 0.5, now: 0.0);

        Assert.IsTrue(detector.InTarget);
        Assert.AreEqual(0, detector.Overshoots);
    }

    [TestMethod]
    public void Update_AfterComplete_IsNoOp()
    {
        // Once complete, further Update calls return immediately: no overshoot even if distance is huge.
        var detector = new DwellDetector(dwellTime: 1.0, threshold: 0.5);
        detector.Update(distance: 0.1, now: 0.0);
        detector.Update(distance: 0.1, now: 1.0); // completes here
        Assert.IsTrue(detector.IsComplete);

        detector.Update(distance: 5.0, now: 2.0); // way out of target, but ignored

        Assert.IsTrue(detector.IsComplete);
        Assert.AreEqual(0, detector.Overshoots);         // no-op => no overshoot recorded
        Assert.AreEqual(1.0, detector.HoldProgress, 1e-9); // unchanged since completion
    }

    [TestMethod]
    public void Reset_AfterActivity_ClearsAllState()
    {
        // Build up non-trivial state (in target, one overshoot), then Reset back to fresh.
        var detector = new DwellDetector(dwellTime: 1.0, threshold: 0.5);
        detector.Update(distance: 0.2, now: 0.0);
        detector.Update(distance: 0.9, now: 0.5); // Overshoots=1
        detector.Update(distance: 0.2, now: 1.0); // back in target

        detector.Reset();

        Assert.IsFalse(detector.InTarget);
        Assert.AreEqual(0.0, detector.HoldTime, 1e-9);
        Assert.AreEqual(0.0, detector.HoldProgress, 1e-9);
        Assert.AreEqual(0, detector.Overshoots);
        Assert.IsFalse(detector.IsComplete);
        // DwellTime / Threshold are read-only config and are not affected by Reset.
        Assert.AreEqual(1.0, detector.DwellTime, 1e-9);
        Assert.AreEqual(0.5, detector.Threshold, 1e-9);
    }
}
