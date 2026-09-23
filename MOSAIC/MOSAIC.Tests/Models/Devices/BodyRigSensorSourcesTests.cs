using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models;
using MOSAIC.Models.Devices;
using MOSAIC.Models.FlowControl;
using MOSAIC.ViewModels.Devices;
using BodyRigBlock = MOSAIC.Models.Devices.BodyRig;

namespace MOSAIC.Tests.Models.Devices;

/// <summary>
/// Covers recovering each sensor slot's physical peripheral number from the pipeline graph.
/// </summary>
/// <remarks>
/// <para>
/// A rig driven from upstream receives a concatenated vector of quaternions and cannot tell which
/// ESP node produced any given group — that fact lives in the configuration, where the firmware
/// encodes each node's number in its UDP port ("11" + number, so node 18 transmits on 11018).
/// </para>
/// <para>
/// Without this, the card can only show positional slots, which is why the lab protocol asks
/// participants to write the slot-to-node mapping down on paper before they can read the card.
/// </para>
/// <para>
/// The sockets here are stubs rather than real <c>UDPClient</c>s: that block binds its port in
/// its constructor, and these tests need specific port numbers, which would make them fight the
/// machine's real rig traffic and each other.
/// </para>
/// </remarks>
[TestClass]
public class BodyRigSensorSourcesTests
{
    /// <summary>A block that owns a receive port, without binding one.</summary>
    private sealed class FakeSocket(string name, int port) : BaseBlock(name), IReceivePort
    {
        public int ReceivePort { get; } = port;

        protected override void OnReceive(object sender, object value) { }
    }

    private static Dictionary<string, BaseBlock> Graph(params BaseBlock[] blocks) =>
        blocks.ToDictionary(b => b.Name, b => b, StringComparer.Ordinal);

    /// <summary>A projector picking a quaternion out of one node's packet.</summary>
    private static Projector Quaternion(string name, string source) =>
        new(name, [0, 1, 2, 3]) { Inputs = [source] };

    // ── The shape the examples actually use ─────────────────────────────────

    [TestMethod]
    public void TwoSensorJoinerPipeline_NamesBothSlots()
    {
        using var rx18 = new FakeSocket("sensor18Reading", 11018);
        using var rx92 = new FakeSocket("sensor92Reading", 11092);
        using var rot18 = Quaternion("sensor18ROT", "sensor18Reading");
        using var rot92 = Quaternion("sensor92ROT", "sensor92Reading");
        using var joiner = new Joiner("quats", 0, "sensor18ROT") { Inputs = ["sensor18ROT", "sensor92ROT"] };
        using var rig = new BodyRigBlock { Inputs = ["quats"] };

        var slots = BodyRigSensorSources.Resolve(rig, Graph(rx18, rx92, rot18, rot92, joiner));

        Assert.HasCount(2, slots);
        Assert.AreEqual(18, slots[0].PeripheralId);
        Assert.AreEqual(92, slots[1].PeripheralId);
        Assert.AreEqual("sensor18ROT", slots[0].SourceName);
    }

    [TestMethod]
    public void SlotOrder_FollowsTheJoinersDeclaredInputs_NotDictionaryOrder()
    {
        // The Joiner concatenates in declared order, so the slots have to be derived from the
        // same list. Taking whatever order the lookup happens to yield would put a segment on
        // the wrong limb, and only sometimes.
        using var rx18 = new FakeSocket("sensor18Reading", 11018);
        using var rx92 = new FakeSocket("sensor92Reading", 11092);
        using var rot18 = Quaternion("sensor18ROT", "sensor18Reading");
        using var rot92 = Quaternion("sensor92ROT", "sensor92Reading");
        using var joiner = new Joiner("quats", 0, "sensor92ROT") { Inputs = ["sensor92ROT", "sensor18ROT"] };
        using var rig = new BodyRigBlock { Inputs = ["quats"] };

        // Dictionary populated in the opposite order to the declaration.
        var slots = BodyRigSensorSources.Resolve(rig, Graph(rot18, rx18, rot92, rx92, joiner));

        Assert.AreEqual(92, slots[0].PeripheralId, "declared first, so slot 0");
        Assert.AreEqual(18, slots[1].PeripheralId);
    }

    [TestMethod]
    public void SingleSensorPipeline_WithNoJoiner_StillResolves()
    {
        using var rx = new FakeSocket("sensor1Reading", 11018);
        using var rot = Quaternion("sensor1ROT", "sensor1Reading");
        using var rig = new BodyRigBlock { Inputs = ["sensor1ROT"] };

        var slots = BodyRigSensorSources.Resolve(rig, Graph(rx, rot));

        Assert.HasCount(1, slots);
        Assert.AreEqual(18, slots[0].PeripheralId);
    }

    [TestMethod]
    public void TheWalkPassesThroughAnyNumberOfIntermediateBlocks()
    {
        // Real pipelines put filters and resamplers between the socket and the rig.
        using var rx = new FakeSocket("rx", 11007);
        using var a = Quaternion("a", "rx");
        using var b = Quaternion("b", "a");
        using var c = Quaternion("c", "b");
        using var rig = new BodyRigBlock { Inputs = ["c"] };

        var slots = BodyRigSensorSources.Resolve(rig, Graph(rx, a, b, c));

        Assert.AreEqual(7, slots[0].PeripheralId);
    }

    // ── Cases it must refuse to guess about ─────────────────────────────────

    [TestMethod]
    public void ASourceWithNoSocketUpstream_IsListedWithoutAPeripheral()
    {
        // The slot still exists and still binds; only its identity is unknown. Dropping it
        // would hide a real sensor, and inventing a number would be worse than saying nothing.
        using var rot = Quaternion("fromFile", "player");
        using var rig = new BodyRigBlock { Inputs = ["fromFile"] };

        var slots = BodyRigSensorSources.Resolve(rig, Graph(rot));

        Assert.HasCount(1, slots);
        Assert.IsNull(slots[0].PeripheralId);
        Assert.AreEqual("S0", slots[0].Label, "falls back to the bare slot");
    }

    [TestMethod]
    public void APortOutsideTheBodyRigRange_YieldsNoPeripheral()
    {
        // 3342 is MOSAIC's default UDP port, not a rig node. Subtracting 11000 would give a
        // negative "node number", which is nonsense worth refusing rather than displaying.
        using var rx = new FakeSocket("rx", 3342);
        using var rot = Quaternion("rot", "rx");
        using var rig = new BodyRigBlock { Inputs = ["rot"] };

        var slots = BodyRigSensorSources.Resolve(rig, Graph(rx, rot));

        Assert.IsNull(slots[0].PeripheralId);
    }

    [TestMethod]
    public void ARigWithNoInputs_ResolvesToNothing()
    {
        using var rig = new BodyRigBlock();

        Assert.IsEmpty(BodyRigSensorSources.Resolve(rig, Graph()));
    }

    [TestMethod]
    public void ACycleInTheGraph_Terminates()
    {
        // A hand-edited config can name blocks in a loop. This runs on the UI thread during
        // pipeline load, so it has to stop rather than hang.
        using var a = Quaternion("a", "b");
        using var b = Quaternion("b", "a");
        using var rig = new BodyRigBlock { Inputs = ["a"] };

        var slots = BodyRigSensorSources.Resolve(rig, Graph(a, b));

        Assert.HasCount(1, slots);
        Assert.IsNull(slots[0].PeripheralId);
    }

    [TestMethod]
    public void AWideSource_OccupiesOneSlotPerQuaternion_AllFromTheSameNode()
    {
        // A projector taking eight values off one packet fills two slots, and both of them
        // genuinely came off that node's socket.
        using var rx = new FakeSocket("rx", 11018);
        using var wide = new Projector("wide", [0, 1, 2, 3, 4, 5, 6, 7]) { Inputs = ["rx"] };
        using var rig = new BodyRigBlock { Inputs = ["wide"] };

        var slots = BodyRigSensorSources.Resolve(rig, Graph(rx, wide));

        Assert.HasCount(2, slots);
        Assert.AreEqual(18, slots[0].PeripheralId);
        Assert.AreEqual(18, slots[1].PeripheralId);
        Assert.AreEqual(1, slots[1].Slot);
    }

    // ── Labels ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Label_CarriesBothTheSlotAndThePeripheral()
    {
        var source = new BodyRigSensorSource(1, 92, "sensor92ROT");

        Assert.AreEqual("S1 · #92", source.Label);
        Assert.AreEqual(11092, source.UdpPort, "the port the node transmits on");
    }

    [TestMethod]
    public void Label_WithoutAPeripheral_IsJustTheSlot()
    {
        var source = new BodyRigSensorSource(3, null, "somewhere");

        Assert.AreEqual("S3", source.Label);
        Assert.IsNull(source.UdpPort);
    }

    // ── What the card ends up showing ───────────────────────────────────────

    [TestMethod]
    public void TheChainStripAndBindDropdown_ShowThePeripheralNumber()
    {
        using var rig = new BodyRigBlock();
        rig.NumOfChannels = 2;
        rig.DescribeSensorSources(
        [
            new BodyRigSensorSource(0, 18, "sensor18ROT"),
            new BodyRigSensorSource(1, 92, "sensor92ROT")
        ]);

        var vm = new BodyRigViewModel(rig);

        // Segment 0 is pre-bound to slot 0, segment 1 to slot 1.
        Assert.AreEqual("S0 · #18", vm.Chain[0].SensorLabel);
        Assert.AreEqual("S1 · #92", vm.Chain[1].SensorLabel);

        // Index 0 of the dropdown is the "no sensor" dash.
        Assert.AreEqual("S0 · #18", vm.SensorChoiceLabels[1]);
        Assert.AreEqual("S1 · #92", vm.SensorChoiceLabels[2]);
    }

    [TestMethod]
    public void TheTooltip_NamesTheNodeThePortAndTheSourceBlock()
    {
        using var rig = new BodyRigBlock();
        rig.NumOfChannels = 1;
        rig.DescribeSensorSources([new BodyRigSensorSource(0, 18, "sensor18ROT")]);

        var vm = new BodyRigViewModel(rig);

        StringAssert.Contains(vm.Chain[0].SensorTooltip, "#18");
        StringAssert.Contains(vm.Chain[0].SensorTooltip, "11018");
        StringAssert.Contains(vm.Chain[0].SensorTooltip, "sensor18ROT");
    }

    [TestMethod]
    public void WithNoSourcesDescribed_TheCardFallsBackToBareSlots()
    {
        // A rig dropped in from the palette has no pipeline behind it yet.
        using var rig = new BodyRigBlock();
        rig.NumOfChannels = 2;

        var vm = new BodyRigViewModel(rig);

        Assert.AreEqual("S0", vm.Chain[0].SensorLabel);
        Assert.AreEqual("S1", vm.Chain[1].SensorLabel);
    }
}
