using System.Collections.Generic;
using System.Threading;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="Trigger"/>.
///
/// <para>
/// The Trigger is a source block (MinInputs = MaxInputs = 0) whose <c>OnReceive</c> is a no-op, so the
/// standard <see cref="MOSAIC.Tests.TestSupport.BlockHarness"/> (which drives via <c>ReceiveInput</c>)
/// cannot make it publish. Instead the block emits through <see cref="Trigger.StartCapture"/> /
/// <see cref="Trigger.StopCapture"/>. These tests attach a small <see cref="RecordingSubscriber"/>
/// directly, invoke the capture lifecycle, and assert on the published values. Delivery is asynchronous
/// through the per-subscriber FIFO pump in <c>OutputDispatcher</c>, so the subscriber blocks on a
/// semaphore until the expected number of values has actually arrived.
/// </para>
///
/// <para>Published-value contract (read from the source):</para>
/// <list type="bullet">
///   <item><description>Classic mode: <c>StartCapture</c> publishes the current action vector verbatim, once.</description></item>
///   <item><description>Oscillation mode: <c>StartCapture</c> first publishes <c>baseVec * sin^2(2*pi*f*0) = baseVec * 0</c>, an all-zero vector of the same length.</description></item>
///   <item><description><c>StopCapture</c> publishes <see langword="null"/> to finalise the segment.</description></item>
///   <item><description>With no actions, <c>StartCapture</c> returns before publishing anything.</description></item>
/// </list>
/// </summary>
[TestClass]
public class TriggerTests
{
    /// <summary>
    /// Records every value delivered to it (including <see langword="null"/>) in publish order and
    /// lets a test thread block until at least N values have arrived. The dispatcher guarantees
    /// single-reader, in-order delivery per subscriber, so the recorded index maps to publish order.
    /// </summary>
    private sealed class RecordingSubscriber : ISubscriber
    {
        private readonly object _lock = new();
        private readonly List<object?> _values = new();
        private readonly SemaphoreSlim _arrived = new(0);

        public void ReceiveInput(object sender, object value)
        {
            lock (_lock) _values.Add(value);
            _arrived.Release();
        }

        /// <summary>Blocks until at least <paramref name="count"/> values have been delivered, or times out.</summary>
        public bool WaitForCount(int count, int timeoutMs = 2000)
        {
            for (int i = 0; i < count; i++)
                if (!_arrived.Wait(timeoutMs)) return false;
            return true;
        }

        public int Count
        {
            get { lock (_lock) return _values.Count; }
        }

        public object? Value(int index)
        {
            lock (_lock) return _values[index];
        }
    }

    // ---------------------------------------------------------------- Publishing (classic mode)

    [TestMethod]
    public void StartCapture_ClassicMode_PublishesCurrentActionVector()
    {
        // Single action "thumb" -> [1,0,0,0,0]; CurrentIdx defaults to 0 so that action is current.
        // Classic mode publishes the action vector verbatim, exactly once.
        using var block = new Trigger(name: "T");
        block.Actions["thumb"] = Vector<double>.Build.Dense(new[] { 1.0, 0.0, 0.0, 0.0, 0.0 });
        var sub = new RecordingSubscriber();
        block.AddSubscriber(sub);

        block.StartCapture();

        Assert.IsTrue(sub.WaitForCount(1), "Trigger did not publish on StartCapture.");
        var published = sub.Value(0) as Vector<double>;
        Assert.IsNotNull(published, "Published value was not a Vector<double>.");
        Assert.AreEqual(5, published!.Count);      // same dimensionality as the action vector
        Assert.AreEqual(1.0, published[0], 1e-9);  // active channel, published unchanged
        Assert.AreEqual(0.0, published[1], 1e-9);
        Assert.AreEqual(0.0, published[2], 1e-9);
        Assert.AreEqual(0.0, published[3], 1e-9);
        Assert.AreEqual(0.0, published[4], 1e-9);
    }

    [TestMethod]
    public void StartCapture_AfterNextAction_PublishesNewlySelectedVector()
    {
        // Actions inserted in order: rest(idx0) -> [0,0,0], thumb(idx1) -> [1,0,0].
        // NextAction advances CurrentIdx from 0 to (0+1) % 2 = 1, so "thumb" is current and published.
        using var block = new Trigger(name: "T");
        block.Actions["rest"]  = Vector<double>.Build.Dense(new[] { 0.0, 0.0, 0.0 });
        block.Actions["thumb"] = Vector<double>.Build.Dense(new[] { 1.0, 0.0, 0.0 });
        var sub = new RecordingSubscriber();
        block.AddSubscriber(sub);

        block.NextAction();
        block.StartCapture();

        Assert.IsTrue(sub.WaitForCount(1), "Trigger did not publish on StartCapture.");
        var published = sub.Value(0) as Vector<double>;
        Assert.IsNotNull(published);
        Assert.AreEqual(3, published!.Count);
        Assert.AreEqual(1.0, published[0], 1e-9);  // thumb's active channel
        Assert.AreEqual(0.0, published[1], 1e-9);
        Assert.AreEqual(0.0, published[2], 1e-9);
        Assert.AreEqual("thumb", block.CurrentActionName);
    }

    [TestMethod]
    public void StartStopCapture_ClassicMode_PublishesVectorThenNull()
    {
        // Lifecycle: StartCapture publishes the action vector; StopCapture publishes null to finalise
        // the segment. Per-subscriber delivery is FIFO, so the recorded order is [vector, null].
        using var block = new Trigger(name: "T");
        block.Actions["thumb"] = Vector<double>.Build.Dense(new[] { 1.0, 0.0 });
        var sub = new RecordingSubscriber();
        block.AddSubscriber(sub);

        block.StartCapture();
        block.StopCapture();

        Assert.IsTrue(sub.WaitForCount(2), "Expected two publishes (vector then null).");
        var first = sub.Value(0) as Vector<double>;
        Assert.IsNotNull(first);
        Assert.AreEqual(2, first!.Count);
        Assert.AreEqual(1.0, first[0], 1e-9);
        Assert.AreEqual(0.0, first[1], 1e-9);
        Assert.IsNull(sub.Value(1));  // StopCapture publishes null as the segment terminator
    }

    // ---------------------------------------------------------------- Publishing (oscillation mode)

    [TestMethod]
    public void StartCapture_OscillationMode_FirstPublishIsZeroModulatedVector()
    {
        // Oscillation mode: the first frame is PublishModulated(vec, phaseSeconds: 0).
        // envelope = sin^2(2*pi*f*0) = sin^2(0) = 0, so every channel is baseVec[i] * 0 = 0.
        // The frame keeps the action's dimensionality (5) but is all zeros.
        using var block = new Trigger(name: "T") { OscillationMode = true };
        block.Actions["thumb"] = Vector<double>.Build.Dense(new[] { 1.0, 1.0, 0.0, 0.0, 0.0 });
        var sub = new RecordingSubscriber();
        block.AddSubscriber(sub);

        block.StartCapture();
        Assert.IsTrue(sub.WaitForCount(1), "Trigger did not publish the initial oscillation frame.");
        block.StopCapture();  // stop the internal timer so no further frames race the assertions

        var first = sub.Value(0) as Vector<double>;
        Assert.IsNotNull(first);
        Assert.AreEqual(5, first!.Count);
        Assert.AreEqual(0.0, first[0], 1e-9);  // 1 * sin^2(0) = 0
        Assert.AreEqual(0.0, first[1], 1e-9);  // 1 * sin^2(0) = 0
        Assert.AreEqual(0.0, first[2], 1e-9);
        Assert.AreEqual(0.0, first[3], 1e-9);
        Assert.AreEqual(0.0, first[4], 1e-9);
    }

    // ---------------------------------------------------------------- Guard: no actions

    [TestMethod]
    public void StartCapture_NoActions_StaysSilent()
    {
        // Guard: with an empty Actions dictionary StartCapture returns before publishing anything.
        using var block = new Trigger(name: "T");
        var sub = new RecordingSubscriber();
        block.AddSubscriber(sub);

        block.StartCapture();

        Assert.IsFalse(sub.WaitForCount(1, timeoutMs: 500), "Trigger must stay silent when it has no actions.");
        Assert.AreEqual(0, sub.Count);
    }

    // ---------------------------------------------------------------- Action navigation / selection

    [TestMethod]
    public void NextAction_WrapsFromLastBackToFirst()
    {
        // Two actions: rest(idx0), thumb(idx1). NextAction steps 0 -> 1, then (1+1) % 2 = 0, wrapping
        // back to the first action.
        using var block = new Trigger(name: "T");
        block.Actions["rest"]  = Vector<double>.Build.Dense(new[] { 0.0 });
        block.Actions["thumb"] = Vector<double>.Build.Dense(new[] { 1.0 });

        block.NextAction();  // 0 -> 1 (thumb)
        Assert.AreEqual("thumb", block.CurrentActionName);

        block.NextAction();  // 1 -> 0 (wrap back to rest)
        Assert.AreEqual("rest", block.CurrentActionName);
    }

    [TestMethod]
    public void SelectAction_ByName_SelectsThatActionCaseInsensitively()
    {
        // SelectAction matches names case-insensitively. "PINCH" points CurrentIdx at the pinch entry
        // (idx 2), so CurrentActionVector returns pinch's vector [1,1,0].
        using var block = new Trigger(name: "T");
        block.Actions["rest"]  = Vector<double>.Build.Dense(new[] { 0.0, 0.0, 0.0 });
        block.Actions["thumb"] = Vector<double>.Build.Dense(new[] { 1.0, 0.0, 0.0 });
        block.Actions["pinch"] = Vector<double>.Build.Dense(new[] { 1.0, 1.0, 0.0 });

        block.SelectAction("PINCH");

        Assert.AreEqual("pinch", block.CurrentActionName);
        var vec = block.CurrentActionVector;
        Assert.IsNotNull(vec);
        Assert.AreEqual(3, vec!.Count);
        Assert.AreEqual(1.0, vec[0], 1e-9);
        Assert.AreEqual(1.0, vec[1], 1e-9);
        Assert.AreEqual(0.0, vec[2], 1e-9);
    }

    [TestMethod]
    public void CurrentIdx_BeyondCount_ClampedByAccessors()
    {
        // The CurrentActionName / CurrentActionVector accessors clamp CurrentIdx into [0, Count-1] and
        // write the clamped value back. With 2 actions and CurrentIdx = 99, Clamp(99, 0, 1) = 1 => the
        // last action, "thumb".
        using var block = new Trigger(name: "T");
        block.Actions["rest"]  = Vector<double>.Build.Dense(new[] { 0.0, 0.0 });
        block.Actions["thumb"] = Vector<double>.Build.Dense(new[] { 1.0, 0.0 });
        block.CurrentIdx = 99;

        Assert.AreEqual("thumb", block.CurrentActionName);  // accessor clamps 99 -> 1
        Assert.AreEqual(1, block.CurrentIdx);               // and writes the clamped index back

        var vec = block.CurrentActionVector;
        Assert.IsNotNull(vec);
        Assert.AreEqual(2, vec!.Count);
        Assert.AreEqual(1.0, vec[0], 1e-9);
        Assert.AreEqual(0.0, vec[1], 1e-9);
    }
}
