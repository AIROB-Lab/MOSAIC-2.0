using System;
using System.Linq;
using System.Reflection;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.Tests.Components.Basics;

[TestClass]
public class AutomaticStatusTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private sealed class Probe() : BaseBlock("status", 10)
    {
        protected override void OnReceive(object sender, object value) { }
    }

    [TestMethod]
    public void EveryBlock_InheritsTheExistingBaseActivityStatus()
    {
        var activity = typeof(BaseBlock).GetProperty(nameof(BaseBlock.Status))!;
        Assert.IsTrue(activity.GetSetMethod()!.IsPublic, "Preserve the existing observable-property API.");
        Assert.IsNull(typeof(BaseBlock).GetProperty("OperationStatus"));
        var blocks = typeof(BaseBlock).Assembly.GetTypes().Where(type => type.IsSubclassOf(typeof(BaseBlock)));
        Assert.IsTrue(blocks.Any());
        foreach (var block in blocks)
        {
            Assert.AreEqual(typeof(BaseBlock), block.GetProperty(nameof(BaseBlock.Status))!.DeclaringType,
                $"{block.FullName} must not hide the automatic activity status.");
            Assert.IsNull(block.GetProperty("OperationStatus"), block.FullName);
        }
    }

    [DataTestMethod]
    [DataRow(0.1, 0.0, BlockStatus.Normal)]
    [DataRow(1.0, 0.0, BlockStatus.Lagging)]
    [DataRow(3.0, 2.0, BlockStatus.Idle)]
    public void PeriodicRefresh_DerivesActivityFromTimingAndPreservesErrors(
        double firstAge, double lastAge, BlockStatus expected)
    {
        using var block = new Probe();
        var tracker = (TickTracker)typeof(BaseBlock).GetField("_tickTracker", PrivateInstance)!.GetValue(block)!;
        var history = (CircularBuffer<double>)typeof(TickTracker).GetField("_timestamps", PrivateInstance)!.GetValue(tracker)!;
        // Set a known history instead of relying on sleeps or scheduler timing.
        double now = TickTracker.Now();
        history.Add(now - firstAge); history.Add(now - lastAge);
        block.ReportError("A separate validation failure");
        typeof(BaseBlock).GetMethod("RefreshStatus", PrivateInstance)!.Invoke(block, null);
        Assert.AreEqual(expected, block.Status);
        Assert.AreEqual("A separate validation failure", block.LastError);
        block.ClearError();
        Assert.AreEqual(expected, block.Status);
        Assert.IsFalse(block.HasDiagnostics);
    }

    [TestMethod]
    public void Selector_ActivityAndExplicitErrorRecoveryRemainIndependent()
    {
        using var block = new MOSAIC.Models.FlowControl.Selector("selector");
        block.ReceiveInput(this, "unsupported");
        Assert.IsNotNull(block.LastError);
        Assert.AreEqual(BlockStatus.Idle, block.Status);
        block.ReceiveInput(this, ("label", Vector<double>.Build.Dense(2, 1)));
        Assert.IsNotNull(block.LastError, "Publishing data must not silently erase a prior failure.");
        // One published value is insufficient history for the automatic rate estimate.
        typeof(BaseBlock).GetMethod("RefreshStatus", PrivateInstance)!.Invoke(block, null);
        Assert.AreEqual(BlockStatus.Idle, block.Status);
        block.ClearError();
        Assert.IsNull(block.LastError);
        Assert.AreEqual(BlockStatus.Idle, block.Status);
    }
}
