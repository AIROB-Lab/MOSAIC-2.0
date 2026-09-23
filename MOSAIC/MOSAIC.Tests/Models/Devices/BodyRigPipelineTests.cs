using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
using BodyRigBlock = MOSAIC.Models.Devices.BodyRig;

namespace MOSAIC.Tests.Models.Devices;

/// <summary>
/// End-to-end tests for the single-sensor BodyRig 2 topology
/// (<c>UDPClient → Projector → BodyRig</c>), built through the real
/// <see cref="JsonParser"/> and <see cref="BlockGraphBuilder"/> and driven over UDP loopback.
/// </summary>
/// <remarks>
/// <para>
/// This is the wire-format contract with the rig: it sends four little-endian float32s per
/// packet in <c>w, x, y, z</c> order, and <see cref="BodyRigBlock"/> reads a quaternion group
/// as <c>[w, x, y, z]</c>. Nothing else in the suite pins that down, and getting it wrong
/// produces a plausible-looking but wrong pose rather than an error.
/// </para>
/// <para>
/// The tests bind an ephemeral loopback port rather than the rig's real 11001, so they do not
/// collide with a running instance or with each other.
/// </para>
/// </remarks>
[TestClass]
public class BodyRigPipelineTests
{
    private const int ValuesPerSegment = 7;

    /// <summary>Leases a free UDP port by binding port 0 and releasing it again.</summary>
    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    /// <summary>The single-sensor pipeline, with the receive port substituted in.</summary>
    private static string SingleSensorConfig(int port) => $$"""
        {
          "sensor1Reading": {
            "Type": "UDPClient",
            "DesiredRate": 100,
            "Params": [{{port.ToString(CultureInfo.InvariantCulture)}}, "float"]
          },
          "sensor1ROT": {
            "Type": "Projector",
            "Inputs": ["sensor1Reading"],
            "Params": ["0;1;2;3"]
          },
          "bodyRig": {
            "Type": "BodyRig",
            "Inputs": ["sensor1ROT"],
            "Params": ["", 1, ""]
          }
        }
        """;

    private static byte[] PacketOf(QuaternionF q)
    {
        var floats = new[] { q.W, q.X, q.Y, q.Z };
        var bytes = new byte[floats.Length * 4];
        System.Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static BlockGraphBuilder.BuiltGraph BuildGraph(string json)
    {
        var models = JsonParser.Parse(json)
            .ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Key });

        var sp = new ServiceCollection().BuildServiceProvider();
        return BlockGraphBuilder.Build(models, new BlockFactory(sp));
    }

    private static void DisposeGraph(BlockGraphBuilder.BuiltGraph graph)
    {
        foreach (var instance in graph.Instances.Values)
            (instance as IDisposable)?.Dispose();
    }

    // ── Configuration shape ─────────────────────────────────────────────────

    [TestMethod]
    public void SingleSensorConfig_BuildsAndWiresTheWholeChain()
    {
        var graph = BuildGraph(SingleSensorConfig(FreeUdpPort()));
        try
        {
            Assert.IsInstanceOfType<BodyRigBlock>(graph.Instances["bodyRig"]);

            var rig = (BodyRigBlock)graph.Instances["bodyRig"];
            Assert.AreEqual(1, rig.NumOfChannels, "one sensor means a one-segment chain");
            Assert.AreEqual(1, rig.SegmentCount);
            Assert.AreEqual(string.Empty, rig.PortNumber, "the quaternion path must not need a COM port");
        }
        finally { DisposeGraph(graph); }
    }

    [TestMethod]
    public void SingleSensorConfig_LeavesTheSerialPortClosed()
    {
        // The rig streams over UDP here. If the block had tried to open a COM port during
        // configuration this would come back as a connection error instead of the initial state.
        var graph = BuildGraph(SingleSensorConfig(FreeUdpPort()));
        try
        {
            var rig = (BodyRigBlock)graph.Instances["bodyRig"];
            Assert.AreEqual("Ready", rig.Info.Flag);
        }
        finally { DisposeGraph(graph); }
    }

    // ── End to end over loopback ────────────────────────────────────────────

    [TestMethod]
    public void QuaternionOverUdp_ReachesTheRigAndDrivesForwardKinematics()
    {
        int port = FreeUdpPort();
        var graph = BuildGraph(SingleSensorConfig(port));
        try
        {
            var rig = (BodyRigBlock)graph.Instances["bodyRig"];
            rig.SetDhAngles(0, 0, 0, 0);        // no DH offset, so pose == the sensor reading
            rig.SetLinkLength(0, 1, 0, 0);      // unit link along +X

            var capture = new CaptureSubscriber();
            rig.AddSubscriber(capture);

            // 90° yaw should swing the unit link from +X round onto +Y.
            var sent = QuaternionF.FromEulerDegrees(0, 0, 90);
            using (var sender = new UdpClient())
            {
                sender.Send(PacketOf(sent), 16, new IPEndPoint(IPAddress.Loopback, port));

                Assert.IsTrue(capture.Wait(5000),
                    "no pose was published — the UDP packet never made it through the pipeline");
            }

            var pose = (Vector<double>)capture.Captured!;
            Assert.HasCount(ValuesPerSegment, pose);

            Assert.AreEqual(sent.W, pose[0], 1e-4, "W");
            Assert.AreEqual(sent.X, pose[1], 1e-4, "X");
            Assert.AreEqual(sent.Y, pose[2], 1e-4, "Y");
            Assert.AreEqual(sent.Z, pose[3], 1e-4, "Z");

            Assert.AreEqual(0d, pose[4], 1e-4, "posX");
            Assert.AreEqual(1d, pose[5], 1e-4, "posY");
            Assert.AreEqual(0d, pose[6], 1e-4, "posZ");
        }
        finally { DisposeGraph(graph); }
    }

    [TestMethod]
    public void RepeatedPackets_KeepUpdatingThePose()
    {
        int port = FreeUdpPort();
        var graph = BuildGraph(SingleSensorConfig(port));
        try
        {
            var rig = (BodyRigBlock)graph.Instances["bodyRig"];
            rig.SetDhAngles(0, 0, 0, 0);
            rig.SetLinkLength(0, 1, 0, 0);

            using var sender = new UdpClient();
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);

            foreach (float yaw in new[] { 0f, 45f, 90f })
            {
                var capture = new CaptureSubscriber();
                rig.AddSubscriber(capture);

                sender.Send(PacketOf(QuaternionF.FromEulerDegrees(0, 0, yaw)), 16, endpoint);
                Assert.IsTrue(capture.Wait(5000), $"no pose published for yaw {yaw}");

                var pose = (Vector<double>)capture.Captured!;
                Assert.AreEqual(Math.Cos(yaw * Math.PI / 180d), pose[4], 1e-3, $"posX at yaw {yaw}");
                Assert.AreEqual(Math.Sin(yaw * Math.PI / 180d), pose[5], 1e-3, $"posY at yaw {yaw}");
            }
        }
        finally { DisposeGraph(graph); }
    }

    // ── Graph-editor constraints ────────────────────────────────────────────

    [TestMethod]
    public void UdpClient_IsAValidGraphSource()
    {
        // Receive-only is the normal configuration, so the palette and the graph editor have
        // to let it sit at the head of a pipeline with nothing feeding it.
        Assert.IsTrue(BlockConstraints.CanBeSource(typeof(MOSAIC.Models.Streaming.UDPClient)));
    }

    [TestMethod]
    public void UdpClient_StillAcceptsOneInputForSendMode()
    {
        var constraints = BlockConstraints.For(typeof(MOSAIC.Models.Streaming.UDPClient));

        Assert.AreEqual(0, constraints.Min);
        Assert.AreEqual(1, constraints.Max);
    }

    [TestMethod]
    public void BodyRig_AcceptsExactlyOneUpstreamBlock()
    {
        // More than one sensor therefore has to come in through a Joiner, as the legacy
        // configuration did — the rig itself takes a single concatenated quaternion vector.
        var constraints = BlockConstraints.For(typeof(BodyRigBlock));

        Assert.AreEqual(1, constraints.Min);
        Assert.AreEqual(1, constraints.Max);
    }

    /// <summary>Captures the first value published to it.</summary>
    private sealed class CaptureSubscriber : ISubscriber
    {
        private readonly ManualResetEventSlim _received = new(false);
        public object? Captured { get; private set; }

        public void ReceiveInput(object sender, object value)
        {
            Captured = value;
            _received.Set();
        }

        public bool Wait(int timeoutMs) => _received.Wait(timeoutMs);
    }
}
