using System;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.BodyRig;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models;
using BodyRigBlock = MOSAIC.Models.Devices.BodyRig;

namespace MOSAIC.Tests.Models.Devices;

/// <summary>
/// End-to-end tests for the two-sensor BodyRig topology
/// (<c>2 × [UDPClient → Projector] → Joiner → BodyRig</c>), built through the real factory and
/// driven over UDP loopback.
/// </summary>
/// <remarks>
/// The property that matters and is easy to get wrong: a sensor's identity inside the rig is its
/// <em>position</em> in the joined vector, not its ESP device number. Group 0 drives the segments
/// bound to sensor 0, group 1 those bound to sensor 1. These tests pin that mapping, and pin that
/// it follows the Joiner's declared input order rather than whichever packet arrived first.
/// </remarks>
[TestClass]
public class BodyRigTwoSensorTests
{
    private const int ValuesPerSegment = 7;

    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    /// <summary>A 13-float rig packet: quaternion, then accel/gyro/mag padding.</summary>
    private static byte[] Packet(QuaternionF q)
    {
        var f = new[] { q.W, q.X, q.Y, q.Z, 0f, 0f, -9.81f, 0f, 0f, 0f, 0f, 0f, 0f };
        var b = new byte[f.Length * 4];
        System.Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    private static string Config(int portA, int portB) => $$"""
        {
          "sensorAReading": { "Type": "UDPClient", "DesiredRate": 100, "Params": [{{portA}}, "float"] },
          "sensorAROT":     { "Type": "Projector", "Inputs": ["sensorAReading"], "Params": ["0;1;2;3"] },
          "sensorBReading": { "Type": "UDPClient", "DesiredRate": 100, "Params": [{{portB}}, "float"] },
          "sensorBROT":     { "Type": "Projector", "Inputs": ["sensorBReading"], "Params": ["0;1;2;3"] },
          "quats":          { "Type": "Joiner", "Inputs": ["sensorAROT", "sensorBROT"],
                              "Params": ["timerBlockName:sensorAROT"] },
          "bodyRig":        { "Type": "BodyRig", "Inputs": ["quats"], "Params": ["", 2, ""] }
        }
        """;

    private static BlockGraphBuilder.BuiltGraph BuildGraph(string json)
    {
        var models = JsonParser.Parse(json).ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Key });
        return BlockGraphBuilder.Build(models, new BlockFactory(new ServiceCollection().BuildServiceProvider()));
    }

    private static void DisposeGraph(BlockGraphBuilder.BuiltGraph graph)
    {
        foreach (var instance in graph.Instances.Values) (instance as IDisposable)?.Dispose();
    }

    private static void Send(int port, byte[] payload)
    {
        using var tx = new UdpClient();
        tx.Send(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private sealed class Capture : ISubscriber
    {
        private readonly ManualResetEventSlim _got = new(false);
        public Vector<double>? Last { get; private set; }

        public void ReceiveInput(object sender, object value)
        {
            Last = value as Vector<double>;
            _got.Set();
        }

        public bool Wait(int ms) => _got.Wait(ms);
        public void Rearm() => _got.Reset();
    }

    private static void SendPacerUntilPose(int port, byte[] payload, Capture capture)
    {
        // Separate publisher pumps can reorder the two sensors under load. Keep the
        // pacing sensor running, as a real sensor would, until the joined pose arrives.
        // A fixed sleep before one pacing packet cannot guarantee the other pump ran.
        var elapsed = Stopwatch.StartNew();
        do
        {
            Send(port, payload);
            if (capture.Wait(20)) return;
        } while (elapsed.ElapsedMilliseconds < 5000);

        Assert.Fail("No complete two-sensor pose arrived within 5 seconds.");
    }

    // ── Shape ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void TwoSensorConfig_BuildsATwoSegmentChain()
    {
        var graph = BuildGraph(Config(FreePort(), FreePort()));
        try
        {
            var rig = (BodyRigBlock)graph.Instances["bodyRig"];

            Assert.AreEqual(2, rig.SegmentCount);
            Assert.AreEqual(0, rig.GetSensorIndex(0), "segment 0 listens to joined group 0");
            Assert.AreEqual(1, rig.GetSensorIndex(1), "segment 1 listens to joined group 1");
        }
        finally { DisposeGraph(graph); }
    }

    [TestMethod]
    public void BodyRig_AcceptsOnlyOneInput_WhichIsWhyTheJoinerExists()
    {
        var constraints = BlockConstraints.For(typeof(BodyRigBlock));

        Assert.AreEqual(1, constraints.Max,
            "more than one sensor must be merged upstream, not wired straight in");
    }

    // ── End to end ──────────────────────────────────────────────────────────

    [TestMethod]
    public void BothSensors_DriveTheirOwnSegment()
    {
        int portA = FreePort(), portB = FreePort();
        var graph = BuildGraph(Config(portA, portB));
        try
        {
            var rig = (BodyRigBlock)graph.Instances["bodyRig"];
            rig.SetDhAngles(0, 0, 0, 0);
            rig.SetDhAngles(1, 0, 0, 0);

            var capture = new Capture();
            rig.AddSubscriber(capture);

            // Sensor B is 90° yawed; sensor A is level. B must not be published until A
            // arrives, because A paces the Joiner.
            Send(portB, Packet(QuaternionF.FromEulerDegrees(0, 0, 90)));
            SendPacerUntilPose(portA, Packet(QuaternionF.Identity), capture);
            Assert.HasCount(2 * ValuesPerSegment, capture.Last!);

            const float rad2deg = 180f / MathF.PI;
            Assert.AreEqual(0f, QuaternionF.Yaw(rig.GetSegment(0)!.Orientation) * rad2deg, 1e-2f,
                "segment 0 should follow the level sensor");
            Assert.AreEqual(90f, QuaternionF.Yaw(rig.GetSegment(1)!.Orientation) * rad2deg, 1e-2f,
                "segment 1 should follow the yawed sensor");
        }
        finally { DisposeGraph(graph); }
    }

    [TestMethod]
    public void SensorToSegmentMapping_FollowsDeclaredOrder_NotArrivalOrder()
    {
        // The regression this guards: the Joiner used to concatenate in first-arrival order,
        // so restarting with the sensors racing could swap which IMU drove which segment.
        int portA = FreePort(), portB = FreePort();
        var graph = BuildGraph(Config(portA, portB));
        try
        {
            var rig = (BodyRigBlock)graph.Instances["bodyRig"];
            rig.SetDhAngles(0, 0, 0, 0);
            rig.SetDhAngles(1, 0, 0, 0);

            var capture = new Capture();
            rig.AddSubscriber(capture);

            // B first, deliberately — the declared order is still A then B.
            Send(portB, Packet(QuaternionF.FromEulerDegrees(0, 0, 45)));
            SendPacerUntilPose(portA, Packet(QuaternionF.Identity), capture);

            const float rad2deg = 180f / MathF.PI;
            Assert.AreEqual(45f, QuaternionF.Yaw(rig.GetSegment(1)!.Orientation) * rad2deg, 1e-2f,
                "the declared-second sensor must always land on group 1");
        }
        finally { DisposeGraph(graph); }
    }

    [TestMethod]
    public void BothSensorsAreCountedIndependently()
    {
        // Per-sensor counters are what the card's liveness dots read, so a two-sensor rig
        // must produce two distinct counts.
        int portA = FreePort(), portB = FreePort();
        var graph = BuildGraph(Config(portA, portB));
        try
        {
            var rig = (BodyRigBlock)graph.Instances["bodyRig"];
            var capture = new Capture();
            rig.AddSubscriber(capture);

            Send(portB, Packet(QuaternionF.Identity));
            SendPacerUntilPose(portA, Packet(QuaternionF.Identity), capture);

            Assert.IsGreaterThan(0, rig.GetSensorPacketCount(0), "group 0 arrivals");
            Assert.IsGreaterThan(0, rig.GetSensorPacketCount(1), "group 1 arrivals");
        }
        finally { DisposeGraph(graph); }
    }

    [TestMethod]
    public void ASensorThatNeverAppears_HoldsTheRig_RatherThanMisbindingTheOthers()
    {
        // Not a stall for its own sake. With sensor B absent the joined vector is one group
        // short, so B's segment would be driven by whatever was declared after it — and on a
        // longer chain every later sensor shifts too. A rig that publishes nothing is
        // diagnosable; a rig that animates the wrong limb while reporting itself healthy is not.
        int portA = FreePort(), portB = FreePort();
        var graph = BuildGraph(Config(portA, portB));
        try
        {
            var rig = (BodyRigBlock)graph.Instances["bodyRig"];
            var capture = new Capture();
            rig.AddSubscriber(capture);

            Send(portA, Packet(QuaternionF.FromEulerDegrees(0, 0, 30)));

            Assert.IsFalse(capture.Wait(1500),
                "nothing is published while a declared sensor has never spoken");

            var joiner = (Joiner)graph.Instances["quats"];
            Assert.IsTrue(joiner.IsWaitingForInputs);
            CollectionAssert.Contains(joiner.PendingInputs.ToArray(), "sensorBROT",
                "and it names the node that has not come up");
        }
        finally { DisposeGraph(graph); }
    }

    [TestMethod]
    public void ASensorThatGoesQuietMidSession_KeepsTheRigRunning_OnItsLastPose()
    {
        // The dead-battery case, and the one that must NOT stall. Once a node has delivered
        // once its width is known and its last value stands in, so the vector keeps its shape
        // and every segment keeps its slot.
        int portA = FreePort(), portB = FreePort();
        var graph = BuildGraph(Config(portA, portB));
        try
        {
            var rig = (BodyRigBlock)graph.Instances["bodyRig"];
            rig.SetDhAngles(0, 0, 0, 0);
            rig.SetDhAngles(1, 0, 0, 0);

            // Both speak, so the record is complete and the widths are known.
            Send(portB, Packet(QuaternionF.FromEulerDegrees(0, 0, 90)));

            var capture = new Capture();
            rig.AddSubscriber(capture);

            // From here only the pacer: B's battery is flat.
            SendPacerUntilPose(portA, Packet(QuaternionF.FromEulerDegrees(0, 0, 30)), capture);
            Assert.HasCount(2 * ValuesPerSegment, capture.Last!, "the pose keeps its full width");
            Assert.AreEqual(2, rig.NumOfDevices, "both groups are still present");
        }
        finally { DisposeGraph(graph); }
    }

    [TestMethod]
    public void ChainedSegments_PlaceTheSecondTipRelativeToTheFirst()
    {
        // Two sensors is the first configuration where parenting actually shows up in the
        // output: segment 1's position depends on segment 0's.
        int portA = FreePort(), portB = FreePort();
        var graph = BuildGraph(Config(portA, portB));
        try
        {
            var rig = (BodyRigBlock)graph.Instances["bodyRig"];
            rig.SetDhAngles(0, 0, 0, 0);
            rig.SetDhAngles(1, 0, 0, 0);
            rig.SetLinkLength(0, 1, 0, 0);
            rig.SetLinkLength(1, 1, 0, 0);
            rig.SetParent(1, 0);

            var capture = new Capture();
            rig.AddSubscriber(capture);

            Send(portB, Packet(QuaternionF.Identity));
            SendPacerUntilPose(portA, Packet(QuaternionF.Identity), capture);

            var pose = capture.Last!;
            Assert.AreEqual(1d, pose[4], 1e-3, "segment 0 tip at x=1");
            Assert.AreEqual(2d, pose[ValuesPerSegment + 4], 1e-3, "segment 1 tip end-to-end at x=2");
        }
        finally { DisposeGraph(graph); }
    }
}
