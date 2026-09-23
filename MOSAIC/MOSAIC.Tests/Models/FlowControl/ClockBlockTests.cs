using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Tests for <see cref="ClockBlock"/>, the periodic tick-source block.
///
/// <para>
/// ClockBlock generates ticks on a dedicated high-priority background thread. The emission
/// <em>timing</em> is inherently non-deterministic, so these tests never assert on tick counts or
/// intervals. Instead they exercise the behaviours that are deterministic:
/// </para>
/// <list type="bullet">
///   <item><description>Constructor state (Name, DesiredRate, not running).</description></item>
///   <item><description>Source-block input bounds (MinInputs == MaxInputs == 0).</description></item>
///   <item><description>The <see cref="ClockBlock.Running"/> lifecycle via Start/Stop (state is set
///     synchronously; Stop joins the worker thread, so the transitions are deterministic).</description></item>
///   <item><description>Start idempotency (the <c>if (_running) return;</c> guard).</description></item>
///   <item><description>The single reliable timing fact — that a running clock publishes at least one
///     tick — captured with a generous timeout via the same async dispatcher the block uses in
///     production. Each tick carries a <see cref="TickTracker.Now"/> UNIX-like seconds timestamp.</description></item>
///   <item><description>The <see cref="ClockBlock.OnReceive"/> guard: it is a source block and throws
///     if fed upstream data.</description></item>
///   <item><description>The block's override of <c>OnDesiredRateChanged</c>, which <em>force</em>-propagates
///     a runtime rate change down to already-latched subscribers.</description></item>
/// </list>
/// </summary>
[TestClass]
public class ClockBlockTests
{
    /// <summary>Captures the first value delivered by the dispatcher and signals a wait handle.</summary>
    private sealed class TickCapture : ISubscriber
    {
        private readonly ManualResetEventSlim _received = new(false);
        public object? Captured { get; private set; }

        public void ReceiveInput(object sender, object value)
        {
            Captured ??= value; // keep the first tick only
            _received.Set();
        }

        public bool WaitForValue(int timeoutMs) => _received.Wait(timeoutMs);
    }

    /// <summary>
    /// Minimal downstream block that just holds whatever <see cref="BaseBlock.DesiredRate"/> is
    /// pushed onto it, so rate-propagation can be observed. Its <c>OnReceive</c> is never used here.
    /// </summary>
    private sealed class RateProbe : BaseBlock
    {
        public RateProbe(string name) : base(name) { }

        protected override void OnReceive(object sender, object value) { }
    }

    [TestMethod]
    public void Constructor_DefaultParameters_SetsNameRateAndNotRunning()
    {
        // Defaults are name = "Clock", desiredRate = 200. The clock does not start on construction.
        using var block = new ClockBlock();

        Assert.AreEqual("Clock", block.Name);
        Assert.AreEqual(200.0, block.DesiredRate, 1e-9);
        Assert.IsFalse(block.Running);
    }

    [TestMethod]
    public void Constructor_CustomParameters_SetsNameAndRate()
    {
        // Explicit args flow straight into Name and DesiredRate.
        using var block = new ClockBlock(name: "Metronome", desiredRate: 500);

        Assert.AreEqual("Metronome", block.Name);
        Assert.AreEqual(500.0, block.DesiredRate, 1e-9);
        Assert.IsFalse(block.Running);
    }

    [TestMethod]
    public void InputBounds_SourceBlock_AreZero()
    {
        // ClockBlock is a source: it consumes no upstream data, so both bounds are 0.
        using var block = new ClockBlock();

        Assert.AreEqual(0, block.MinInputs);
        Assert.AreEqual(0, block.MaxInputs);
    }

    [TestMethod]
    public void StartStop_TogglesRunningState()
    {
        // Start sets Running true synchronously; Stop clears it and joins the worker => deterministic.
        using var block = new ClockBlock(name: "Clock", desiredRate: 200);

        Assert.IsFalse(block.Running);

        block.Start();
        try
        {
            Assert.IsTrue(block.Running);
        }
        finally
        {
            block.Stop();
        }

        Assert.IsFalse(block.Running);
    }

    [TestMethod]
    public void Start_CalledWhileAlreadyRunning_IsIdempotent()
    {
        // The `if (_running) return;` guard means a second Start is a no-op (no second thread spawned).
        // A single Stop must therefore still fully stop the clock.
        using var block = new ClockBlock(name: "Clock", desiredRate: 200);

        block.Start();
        try
        {
            block.Start(); // second call: guarded no-op
            Assert.IsTrue(block.Running);
        }
        finally
        {
            block.Stop();
        }

        Assert.IsFalse(block.Running);
    }

    [TestMethod]
    public void Start_WhenRunning_PublishesTimestampTick()
    {
        // The one reliable timing fact: a running clock publishes ticks. At 200 Hz a tick is expected
        // every ~5 ms, so within a 2 s window we are guaranteed at least one. The payload is
        // TickTracker.Now() — a UNIX-like seconds timestamp (well above 1e9 for any modern date).
        using var block = new ClockBlock(name: "Clock", desiredRate: 200);
        var capture = new TickCapture();
        block.AddSubscriber(capture);

        block.Start();
        bool got;
        try
        {
            got = capture.WaitForValue(2000);
        }
        finally
        {
            block.Stop();
        }

        Assert.IsTrue(got, "Running ClockBlock did not publish a tick within the timeout.");
        Assert.IsInstanceOfType(capture.Captured, typeof(double));
        Assert.IsTrue((double)capture.Captured! > 1e9,
            "Published tick should be a UNIX-like seconds timestamp (> 1e9).");
    }

    [TestMethod]
    public void OnReceive_WhenFedUpstreamData_ThrowsNotImplemented()
    {
        // Source block: feeding it input reaches the protected OnReceive (via ReceiveInput) which always
        // throws. A non-BaseBlock sender skips rate inheritance, so the throw is the observed behaviour.
        using var block = new ClockBlock();

        Assert.ThrowsExactly<NotImplementedException>(() => block.ReceiveInput(new object(), 0.0));
    }

    [TestMethod]
    public void DesiredRateChangedAtRuntime_ForcePropagatesToLatchedSubscriber()
    {
        // ClockBlock overrides OnDesiredRateChanged to FORCE-propagate a new rate downstream, overriding
        // a rate a subscriber already latched. A plain BaseBlock would soft-propagate and leave the
        // subscriber's latched 50 Hz untouched; ClockBlock overwrites it with the new 300 Hz.
        using var clock = new ClockBlock(name: "Clock", desiredRate: 200);
        using var probe = new RateProbe("Downstream");

        probe.DesiredRate = 50;              // subscriber latches its own rate first
        clock.AddSubscriber(probe);          // eager propagation must NOT override the latched 50
        Assert.AreEqual(50.0, probe.DesiredRate, 1e-9);

        clock.DesiredRate = 300;             // runtime change => forced down the chain

        Assert.AreEqual(300.0, probe.DesiredRate, 1e-9);
    }
}
