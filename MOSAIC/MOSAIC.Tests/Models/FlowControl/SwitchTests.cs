using System.Collections.Concurrent;
using System.Threading;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.FlowControl;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="Switch"/>.
///
/// The switch has exactly two inputs. Senders are identified by reference on first arrival:
/// the first distinct sender becomes input 0, the second distinct sender becomes input 1.
/// A value is forwarded (published unchanged) only when the sender's input index matches the
/// active input — <c>(sender is input 1) == UseSecondInput</c>; otherwise it is silently dropped.
///
/// <para>
/// <see cref="BlockHarness"/> always feeds with <c>sender == block</c>, so it can only exercise
/// input 0. To drive the full two-input routing, the routing tests below add a local
/// <see cref="Probe"/> subscriber and call the public <see cref="Switch.ReceiveInput"/> with two
/// distinct sender objects — the same reference-identity contract the block uses internally.
/// </para>
/// </summary>
[TestClass]
public class SwitchTests
{
    /// <summary>
    /// Minimal <see cref="ISubscriber"/> that records every value delivered (in publish order) and
    /// lets a test wait for the next one. Mirrors the harness's private capture subscriber but keeps a
    /// queue so a test can both grab a forwarded value and prove nothing else was published.
    /// </summary>
    private sealed class Probe : ISubscriber
    {
        private readonly SemaphoreSlim _delivered = new(0);
        private readonly ConcurrentQueue<object> _values = new();

        public void ReceiveInput(object sender, object value)
        {
            _values.Enqueue(value);
            _delivered.Release();
        }

        /// <summary>Returns the next published value, or <see langword="null"/> if none arrives in time.</summary>
        public object? WaitNext(int timeoutMs)
            => _delivered.Wait(timeoutMs) && _values.TryDequeue(out var v) ? v : null;
    }

    // ---------------------------------------------------------------- input bounds

    [TestMethod]
    public void InputBounds_RequireExactlyTwoInputs()
    {
        // A switch forwards from one of two upstream inputs => min and max are both fixed at 2.
        using var block = new Switch(name: "sw");

        Assert.AreEqual(2, block.MinInputs);
        Assert.AreEqual(2, block.MaxInputs);
    }

    // ---------------------------------------------------------------- gating via the harness (input 0)

    [TestMethod]
    public void OnReceive_DefaultState_PublishesActiveInput()
    {
        // Default UseSecondInput = false => input 0 is active. The harness feeds with sender == block,
        // which is claimed as input 0, so the value is forwarded unchanged (same 3-length vector).
        using var block = new Switch(name: "sw");
        var input = Vector<double>.Build.Dense(new[] { 1.0, 2.0, 3.0 });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(1.0, result[0], 1e-9);
        Assert.AreEqual(2.0, result[1], 1e-9);
        Assert.AreEqual(3.0, result[2], 1e-9);
    }

    [TestMethod]
    public void OnReceive_SecondInputActive_DropsInactiveInput()
    {
        // Activate input 1, then feed from the harness sender (claimed as input 0). The active input (1)
        // differs from the sender's input (0), so the value is dropped and nothing is published.
        using var block = new Switch(name: "sw");
        block.SetActiveInput(1);
        var input = Vector<double>.Build.Dense(new[] { 1.0, 2.0, 3.0 });

        bool published = BlockHarness.TryCapture(block, input, out _);

        Assert.IsFalse(published);
    }

    // ---------------------------------------------------------------- two-sender routing

    [TestMethod]
    public void OnReceive_DefaultState_ForwardsInput0AndDropsInput1()
    {
        // Default: input 0 active. First distinct sender => input 0, second => input 1.
        // Payload from input 0 is forwarded; payload from input 1 is silently dropped.
        using var block = new Switch(name: "sw");
        var probe = new Probe();
        block.AddSubscriber(probe);
        object source0 = new();
        object source1 = new();
        object payload0 = new();
        object payload1 = new();

        block.ReceiveInput(source0, payload0); // claimed as input 0 (active) => forwarded
        block.ReceiveInput(source1, payload1); // claimed as input 1 (inactive) => dropped

        Assert.AreSame(payload0, probe.WaitNext(2000)); // the forwarded reference is exactly payload0
        Assert.IsNull(probe.WaitNext(500));             // input 1 produced no further publish
    }

    [TestMethod]
    public void OnReceive_SecondInputActive_ForwardsInput1AndDropsInput0()
    {
        // Activate input 1. Sender identity is still assigned by arrival order (source0 => input 0,
        // source1 => input 1), independent of which input is active. Only input 1's payload is forwarded.
        using var block = new Switch(name: "sw");
        block.SetActiveInput(1);
        var probe = new Probe();
        block.AddSubscriber(probe);
        object source0 = new();
        object source1 = new();
        object payload0 = new();
        object payload1 = new();

        block.ReceiveInput(source0, payload0); // claimed as input 0 (inactive) => dropped
        block.ReceiveInput(source1, payload1); // claimed as input 1 (active)   => forwarded

        Assert.AreSame(payload1, probe.WaitNext(2000)); // the forwarded reference is exactly payload1
        Assert.IsNull(probe.WaitNext(500));             // input 0 produced no publish
    }

    // ---------------------------------------------------------------- control methods + event

    [TestMethod]
    public void Toggle_FlipsActiveInputAndRaisesInputSwitched()
    {
        using var block = new Switch(name: "sw");
        bool? raisedWith = null;
        block.InputSwitched += v => raisedWith = v;

        // Default UseSecondInput = false. First toggle flips it to true and reports the new value.
        block.Toggle();

        Assert.IsTrue(block.UseSecondInput);
        Assert.IsTrue(raisedWith.HasValue);
        Assert.IsTrue(raisedWith!.Value);

        // Second toggle flips it back to false and reports false.
        raisedWith = null;
        block.Toggle();

        Assert.IsFalse(block.UseSecondInput);
        Assert.IsTrue(raisedWith.HasValue);
        Assert.IsFalse(raisedWith!.Value);
    }

    [DataTestMethod]
    [DataRow(0, false)] // index 0 => input 0 active => UseSecondInput false
    [DataRow(1, true)]  // index 1 => input 1 active => UseSecondInput true
    public void SetActiveInput_SelectsInputByIndexAndRaisesInputSwitched(int index, bool expected)
    {
        // SetActiveInput maps index == 1 to UseSecondInput; any other index maps to false.
        using var block = new Switch(name: "sw");
        bool? raisedWith = null;
        block.InputSwitched += v => raisedWith = v;

        block.SetActiveInput(index);

        Assert.AreEqual(expected, block.UseSecondInput);
        Assert.IsTrue(raisedWith.HasValue);
        Assert.AreEqual(expected, raisedWith!.Value);
    }
}
