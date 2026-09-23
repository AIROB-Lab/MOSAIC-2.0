using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.Streaming;

namespace MOSAIC.Tests.Models.Streaming;

/// <summary>
/// Receive-path tests for <see cref="UDPClient"/>, driven over real loopback sockets.
/// </summary>
/// <remarks>
/// The block binds <see cref="IPAddress.Any"/>, so the receive port is the only thing that
/// selects traffic — a sender's address is irrelevant. These tests pin that behaviour, and
/// that the async receive loop re-arms rather than delivering exactly one packet and stopping.
/// </remarks>
[TestClass]
public class UDPClientReceiveTests
{
    /// <summary>Leases a free UDP port by binding port 0 and releasing it again.</summary>
    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static UDPClient Receiver(int port) =>
        UDPClient.ConfigureInput(
            new ServiceCollection().BuildServiceProvider(),
            new JsonModel { Type = "UDPClient", Name = "rx", DesiredRate = 100, Params = [port, "float"] });

    /// <summary>A 13-float BodyRig-2 style packet: quaternion, accel, gyro, mag.</summary>
    private static byte[] RigPacket(float w, float x, float y, float z)
    {
        var f = new[] { w, x, y, z, -8.4102f, 0.1914f, -4.7930f, 0f, 0f, 0f, -67.125f, 114.6875f, 106.6875f };
        var b = new byte[f.Length * 4];
        System.Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    private sealed class Capture : ISubscriber
    {
        private readonly CountdownEvent _latch;
        public Vector<double>? Last { get; private set; }
        public int Count { get; private set; }

        public Capture(int expected) => _latch = new CountdownEvent(expected);

        public void ReceiveInput(object sender, object value)
        {
            Last = value as Vector<double>;
            Count++;
            if (!_latch.IsSet) _latch.Signal();
        }

        public bool Wait(int ms) => _latch.Wait(ms);
    }

    private static void Send(int port, byte[] payload)
    {
        using var tx = new UdpClient();
        tx.Send(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    [TestMethod]
    public void ReceivesAPacketAndPublishesIt()
    {
        int port = FreePort();
        using var rx = Receiver(port);
        var capture = new Capture(1);
        rx.AddSubscriber(capture);

        Send(port, RigPacket(0.4565f, -0.3459f, 0.7920f, 0.2115f));

        Assert.IsTrue(capture.Wait(5000), "no packet was published");
        Assert.IsNotNull(capture.Last);
        Assert.HasCount(13, capture.Last!);
        Assert.AreEqual(0.4565, capture.Last![0], 1e-4, "w");
        Assert.AreEqual(-0.3459, capture.Last![1], 1e-4, "x");
        Assert.AreEqual(0.7920, capture.Last![2], 1e-4, "y");
        Assert.AreEqual(0.2115, capture.Last![3], 1e-4, "z");
    }

    [TestMethod]
    public void IncrementsTheReceivedCounters()
    {
        // This is the "RECEIVED n pkts" readout on the card.
        int port = FreePort();
        using var rx = Receiver(port);
        var capture = new Capture(1);
        rx.AddSubscriber(capture);

        Send(port, RigPacket(1, 0, 0, 0));
        Assert.IsTrue(capture.Wait(5000));

        Assert.AreEqual(1L, rx.PacketsReceived);
        Assert.AreEqual(52L, rx.BytesReceived);
    }

    [TestMethod]
    public void KeepsReceivingAfterTheFirstPacket()
    {
        // The receive loop must re-arm; if it did not, the card would show 1 packet
        // and then sit at "Idle" forever no matter how much the rig sends.
        int port = FreePort();
        using var rx = Receiver(port);
        var capture = new Capture(5);
        rx.AddSubscriber(capture);

        for (int i = 0; i < 5; i++)
        {
            Send(port, RigPacket(1, 0, 0, 0));
            Thread.Sleep(20);
        }

        Assert.IsTrue(capture.Wait(5000), $"only {capture.Count} of 5 packets were published");
        Assert.AreEqual(5L, rx.PacketsReceived);
    }

    [TestMethod]
    public void AcceptsTrafficFromAnySourceAddress()
    {
        // Bound to IPAddress.Any: the port is the only selector, the sender's address
        // is irrelevant. Sending from an explicitly bound local socket proves the block
        // does not filter on a configured peer.
        int port = FreePort();
        using var rx = Receiver(port);
        var capture = new Capture(1);
        rx.AddSubscriber(capture);

        using (var tx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            var packet = RigPacket(1, 0, 0, 0);
            tx.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
        }

        Assert.IsTrue(capture.Wait(5000), "packet from an unconfigured source was dropped");
        Assert.AreEqual(1L, rx.PacketsReceived);
    }

    [TestMethod]
    public void SurvivesAMalformedPacketAndKeepsListening()
    {
        // 5 bytes is not a whole number of floats. The parse must fail without killing
        // the receive loop, otherwise one stray datagram silently ends the stream.
        int port = FreePort();
        using var rx = Receiver(port);
        var capture = new Capture(1);
        rx.AddSubscriber(capture);

        Send(port, new byte[] { 1, 2, 3, 4, 5 });
        Thread.Sleep(100);
        Send(port, RigPacket(1, 0, 0, 0));

        Assert.IsTrue(capture.Wait(5000), "the loop stopped after a malformed packet");
        Assert.AreEqual(1L, rx.PacketsReceived, "the malformed packet should not be counted");
    }

    [TestMethod]
    public void ReportsItsListeningPort()
    {
        int port = FreePort();
        using var rx = Receiver(port);

        Assert.AreEqual(port, rx.ReceivePort);
        Assert.IsNull(rx.RemoteHost, "receive-only: no remote configured");
    }
}
