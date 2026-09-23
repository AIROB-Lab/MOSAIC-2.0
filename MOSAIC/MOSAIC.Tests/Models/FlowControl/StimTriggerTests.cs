using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.FlowControl;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="StimTrigger"/>.
///
/// State machine (input is a <c>(string state, Vector&lt;double&gt; target)</c> tuple):
///   "capture"                         => publish the target vector,  IsCapturing = true.
///   any other state while capturing   => publish null (stop signal), IsCapturing = false.
///   any other state while NOT capturing => no publish at all (block stays silent).
///
/// Note on delivery semantics: the dispatcher enqueues every Publish(...) call — including
/// Publish(null) — so on the stop transition the harness DOES receive a value (TryCapture
/// returns true) but that value is null. Only the "non-capture while idle" branch produces
/// no Publish at all, so only there does TryCapture return false.
/// </summary>
[TestClass]
public class StimTriggerTests
{
    /// <summary>Builds the <c>(string, Vector&lt;double&gt;)</c> tuple the block expects as input.</summary>
    private static (string, Vector<double>) Signal(string state, params double[] target)
        => (state, Vector<double>.Build.Dense(target));

    [TestMethod]
    public void OnReceive_CaptureState_PublishesTargetVector()
    {
        // "capture" => the target vector is published verbatim (start signal + target payload).
        using var block = new StimTrigger(name: "Trig", desiredRate: 0);
        var input = Signal("capture", 1.0, 2.0, 3.0);

        bool fired = BlockHarness.TryCapture(block, input, out var published);

        Assert.IsTrue(fired, "StimTrigger did not publish on the capture state.");
        Assert.IsInstanceOfType(published, typeof(Vector<double>));
        var vector = (Vector<double>)published!;
        Assert.AreEqual(3, vector.Count);
        Assert.AreEqual(1.0, vector[0], 1e-9);
        Assert.AreEqual(2.0, vector[1], 1e-9);
        Assert.AreEqual(3.0, vector[2], 1e-9);
    }

    [TestMethod]
    public void OnReceive_CaptureState_SetsIsCapturingTrue()
    {
        // Entering "capture" flips the state flag on so the next non-capture state will stop.
        using var block = new StimTrigger(name: "Trig", desiredRate: 0);

        BlockHarness.TryCapture(block, Signal("capture", 5.0), out _);

        Assert.IsTrue(block.IsCapturing);
    }

    [TestMethod]
    public void OnReceive_NonCaptureWhileIdle_StaysSilent()
    {
        // Fresh block is not capturing, so a non-"capture" state is ignored: no Publish at all.
        using var block = new StimTrigger(name: "Trig", desiredRate: 0);

        bool fired = BlockHarness.TryCapture(block, Signal("rest", 9.0), out var published);

        Assert.IsFalse(fired, "StimTrigger published while idle on a non-capture state.");
        Assert.IsNull(published);
        Assert.IsFalse(block.IsCapturing);
    }

    [TestMethod]
    public void OnReceive_CaptureThenNonCapture_PublishesNullStopSignalAndClearsFlag()
    {
        // Capture first (flag => true), then a non-"capture" state emits the null stop signal.
        using var block = new StimTrigger(name: "Trig", desiredRate: 0);
        BlockHarness.TryCapture(block, Signal("capture", 7.0), out _);
        Assert.IsTrue(block.IsCapturing); // precondition for the stop transition

        bool fired = BlockHarness.TryCapture(block, Signal("rest", 7.0), out var published);

        Assert.IsTrue(fired, "Stop transition should publish (a null stop signal).");
        Assert.IsNull(published); // Publish(null) is delivered as a null payload
        Assert.IsFalse(block.IsCapturing);
    }

    [TestMethod]
    public void OnReceive_TwoNonCaptureStatesInARow_StopsOnceThenStaysSilent()
    {
        // capture -> stops on first non-capture (null publish); the SECOND non-capture is
        // now idle again, so it must stay silent (no repeated stop signal).
        using var block = new StimTrigger(name: "Trig", desiredRate: 0);
        BlockHarness.TryCapture(block, Signal("capture", 4.0), out _);

        bool firstStop = BlockHarness.TryCapture(block, Signal("rest", 4.0), out _);
        bool secondStop = BlockHarness.TryCapture(block, Signal("rest", 4.0), out var secondPublished);

        Assert.IsTrue(firstStop, "First non-capture after capture should publish the stop signal.");
        Assert.IsFalse(secondStop, "Second consecutive non-capture should stay silent.");
        Assert.IsNull(secondPublished);
        Assert.IsFalse(block.IsCapturing);
    }

    [TestMethod]
    public void OnReceive_ReCapture_AfterStop_PublishesNewTargetAgain()
    {
        // Full cycle: capture -> stop -> capture again republishes the (new) target vector.
        using var block = new StimTrigger(name: "Trig", desiredRate: 0);
        BlockHarness.TryCapture(block, Signal("capture", 1.0), out _);
        BlockHarness.TryCapture(block, Signal("rest", 1.0), out _);

        bool fired = BlockHarness.TryCapture(block, Signal("capture", 8.0, 9.0), out var published);

        Assert.IsTrue(fired, "Re-entering capture should publish the new target.");
        Assert.IsInstanceOfType(published, typeof(Vector<double>));
        var vector = (Vector<double>)published!;
        Assert.AreEqual(2, vector.Count);
        Assert.AreEqual(8.0, vector[0], 1e-9);
        Assert.AreEqual(9.0, vector[1], 1e-9);
        Assert.IsTrue(block.IsCapturing);
    }

    [TestMethod]
    public void OnReceive_ConsecutiveCaptureStates_RepublishEachTarget()
    {
        // Staying in "capture" republishes every tick (Buffer keeps receiving the live target).
        using var block = new StimTrigger(name: "Trig", desiredRate: 0);

        BlockHarness.TryCapture(block, Signal("capture", 1.0), out _);
        bool fired = BlockHarness.TryCapture(block, Signal("capture", 2.0), out var published);

        Assert.IsTrue(fired, "A second consecutive capture should still publish its target.");
        Assert.IsInstanceOfType(published, typeof(Vector<double>));
        var vector = (Vector<double>)published!;
        Assert.AreEqual(1, vector.Count);
        Assert.AreEqual(2.0, vector[0], 1e-9);
        Assert.IsTrue(block.IsCapturing);
    }
}
