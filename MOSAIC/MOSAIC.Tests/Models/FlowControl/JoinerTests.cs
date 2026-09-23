using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="Joiner"/>.
///
/// The Joiner keeps the most recent payload per source in a dictionary keyed by source name.
/// It only publishes when the input whose source matches the configured timer source arrives;
/// at that moment it flattens every stored payload into one <see cref="Vector{T}"/>:
/// <list type="bullet">
///   <item><description>Vector payloads contribute their elements in order.</description></item>
///   <item><description>Matrix payloads are flattened row-major (row 0 left-to-right, then row 1, ...).</description></item>
///   <item><description>Sources are concatenated in the order they first appeared (dictionary insertion order,
///     which the block relies on — it never removes entries — so it is stable in practice).</description></item>
///   <item><description>Payloads that are neither Vector&lt;double&gt; nor Matrix&lt;double&gt; are ignored entirely
///     (not even stored), so they never affect the output.</description></item>
/// </list>
///
/// Sources are supplied via the legacy tuple protocol the block accepts: an input value of runtime
/// type <c>ValueTuple&lt;string, object&gt;</c> whose first item is the source name. Non-timer sources are
/// fed directly (they store silently); the timer source is fed through <see cref="BlockHarness"/> so the
/// resulting publish is captured.
/// </summary>
[TestClass]
public class JoinerTests
{
    // Builds a payload tagged with a source name using the exact runtime type
    // (ValueTuple<string, object>) the Joiner's legacy-tuple branch matches on.
    private static (string, object) Msg(string source, object payload) => (source, payload);

    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public void Constructor_EmptyOrWhitespaceTimerSource_Throws(string? timerSource)
    {
        // The timer source is mandatory: null collapses to "" (timerSource ?? string.Empty), and
        // IsNullOrWhiteSpace("" / "   ") is true, so the guard throws ArgumentException.
        Assert.ThrowsExactly<ArgumentException>(
            () => new Joiner(name: "J", desiredRate: 0, timerSource: timerSource!));
    }

    [TestMethod]
    public void Constructor_ValidTimerSource_ExposesNameAndInputBounds()
    {
        // A non-empty timer source is stored and exposed; a Joiner accepts 2..int.MaxValue inputs.
        using var block = new Joiner(name: "J", desiredRate: 0, timerSource: "Timer");

        Assert.AreEqual("Timer", block.TimerSourceName);
        Assert.AreEqual(2, block.MinInputs);
        Assert.AreEqual(int.MaxValue, block.MaxInputs);
    }

    [TestMethod]
    public void OnReceive_TimerSourceAlone_PublishesConcatenationOfThatSource()
    {
        // Only the timer source has ever been seen, so the concatenation is exactly that vector.
        // Input [7,8,9] => output [7,8,9], length 3.
        using var block = new Joiner(name: "J", desiredRate: 0, timerSource: "T");

        var result = BlockHarness.CaptureVector(block, Msg("T", Vec(7.0, 8.0, 9.0)));

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(7.0, result[0], 1e-9);
        Assert.AreEqual(8.0, result[1], 1e-9);
        Assert.AreEqual(9.0, result[2], 1e-9);
    }

    [TestMethod]
    public void OnReceive_VectorAndMatrixSources_ConcatenatedRowMajorInInsertionOrder()
    {
        // First-seen order is A (vector), then M (matrix), then T (timer). Concatenation:
        //   A = [1,2]
        //   M = [[3,4],[5,6]] flattened row-major = [3,4,5,6]
        //   T = [7]
        // => [1, 2, 3, 4, 5, 6, 7], length 7.
        using var block = new Joiner(name: "J", desiredRate: 0, timerSource: "T");
        var matrix = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 3.0, 4.0 },
            { 5.0, 6.0 },
        });

        block.ReceiveInput(sender: block, value: Msg("A", Vec(1.0, 2.0))); // stored, no publish
        block.ReceiveInput(sender: block, value: Msg("M", matrix));        // stored, no publish
        var result = BlockHarness.CaptureVector(block, Msg("T", Vec(7.0))); // timer => publish

        Assert.AreEqual(7, result.Count);
        Assert.AreEqual(1.0, result[0], 1e-9); // A[0]
        Assert.AreEqual(2.0, result[1], 1e-9); // A[1]
        Assert.AreEqual(3.0, result[2], 1e-9); // M row0 col0
        Assert.AreEqual(4.0, result[3], 1e-9); // M row0 col1
        Assert.AreEqual(5.0, result[4], 1e-9); // M row1 col0
        Assert.AreEqual(6.0, result[5], 1e-9); // M row1 col1
        Assert.AreEqual(7.0, result[6], 1e-9); // T[0]
    }

    [TestMethod]
    public void OnReceive_RepeatedSource_UsesLatestValue()
    {
        // Source A is fed twice; the second payload overwrites the first, and updating an existing
        // dictionary key preserves its original position, so A stays first in the output.
        //   A latest = [9,9,9], T = [4]  => [9,9,9,4], length 4.
        using var block = new Joiner(name: "J", desiredRate: 0, timerSource: "T");

        block.ReceiveInput(sender: block, value: Msg("A", Vec(1.0, 2.0)));      // stored
        block.ReceiveInput(sender: block, value: Msg("A", Vec(9.0, 9.0, 9.0))); // overwrites A
        var result = BlockHarness.CaptureVector(block, Msg("T", Vec(4.0)));

        Assert.AreEqual(4, result.Count);
        Assert.AreEqual(9.0, result[0], 1e-9);
        Assert.AreEqual(9.0, result[1], 1e-9);
        Assert.AreEqual(9.0, result[2], 1e-9);
        Assert.AreEqual(4.0, result[3], 1e-9);
    }

    [TestMethod]
    public void OnReceive_NonTimerSource_DoesNotPublish()
    {
        // A payload from a non-timer source is stored but must not trigger a publish.
        using var block = new Joiner(name: "J", desiredRate: 0, timerSource: "T");

        bool published = BlockHarness.TryCapture(block, Msg("A", Vec(1.0, 2.0)), out var value);

        Assert.IsFalse(published, "Joiner published on a non-timer source.");
        Assert.IsNull(value);
    }

    [TestMethod]
    public void OnReceive_UnsupportedPayload_IgnoredAndExcludedFromConcatenation()
    {
        // A string payload is neither Vector nor Matrix, so the type guard returns before storing it:
        // source "A" is never recorded. Only B (stored) and T (timer) contribute.
        //   B = [7,7], T = [1] => [7,7,1], length 3 (no trace of the ignored "A").
        using var block = new Joiner(name: "J", desiredRate: 0, timerSource: "T");

        block.ReceiveInput(sender: block, value: Msg("A", "not a vector")); // ignored, not stored
        block.ReceiveInput(sender: block, value: Msg("B", Vec(7.0, 7.0)));  // stored
        var result = BlockHarness.CaptureVector(block, Msg("T", Vec(1.0)));

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(7.0, result[0], 1e-9); // B[0]
        Assert.AreEqual(7.0, result[1], 1e-9); // B[1]
        Assert.AreEqual(1.0, result[2], 1e-9); // T[0]
    }

    [TestMethod]
    public void SetTimerSource_RetargetsTriggerToNewSource()
    {
        // Constructed with timer "T1", then retargeted to "T2". The former timer "T1" no longer
        // triggers (it just stores), and "T2" now drives the publish.
        //   order first-seen: T1 (stored), T2 (timer). T1 = [1,2], T2 = [3] => [1,2,3], length 3.
        using var block = new Joiner(name: "J", desiredRate: 0, timerSource: "T1");
        block.SetTimerSource("T2");

        Assert.AreEqual("T2", block.TimerSourceName);

        bool oldTriggered = BlockHarness.TryCapture(block, Msg("T1", Vec(1.0, 2.0)), out _);
        Assert.IsFalse(oldTriggered, "Old timer source still triggered after retarget.");

        var result = BlockHarness.CaptureVector(block, Msg("T2", Vec(3.0)));

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(1.0, result[0], 1e-9); // T1[0]
        Assert.AreEqual(2.0, result[1], 1e-9); // T1[1]
        Assert.AreEqual(3.0, result[2], 1e-9); // T2[0]
    }
}
