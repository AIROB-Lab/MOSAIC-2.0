using System;
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
using MOSAIC.Tests.TestSupport;
using BodyRigBlock = MOSAIC.Models.Devices.BodyRig;

namespace MOSAIC.Tests.Models.Devices;

/// <summary>
/// Pins the limits of the BodyRig's per-sensor liveness counters in upstream mode.
/// </summary>
/// <remarks>
/// <para>
/// The counters were added so the card could report which IMUs are actually delivering, on the
/// reasoning that a motionless-but-healthy sensor defeats any attempt to infer activity from a
/// changing orientation. That reasoning holds — but only for what the block can see.
/// </para>
/// <para>
/// In upstream mode the block does not see sensors at all. It sees a concatenated vector, and it
/// counts <em>groups of four</em> in that vector. The Joiner republishes on every pacer tick and
/// substitutes the last known value for any source that has gone quiet, so a dead node's group
/// keeps appearing — and keeps incrementing the very counter meant to catch it.
/// </para>
/// <para>
/// These tests document that gap rather than assert it is acceptable. Truthful upstream liveness
/// has to come from the block that owns the socket, or from freshness carried through the Joiner;
/// it cannot be recovered downstream of the concatenation.
/// </para>
/// </remarks>
[TestClass]
public class BodyRigLivenessLimitTests
{
    private static Vector<double> Quaternions(int sensors)
    {
        var data = new double[sensors * 4];
        for (int i = 0; i < sensors; i++) data[i * 4] = 1;
        return Vector<double>.Build.DenseOfArray(data);
    }

    [TestMethod]
    public void UpstreamCounters_CountJoinedGroups_NotSensorPackets()
    {
        // Two groups arrive in one vector, so both counters advance by one — even though the
        // block never observed two independent arrivals.
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;

        BlockHarness.CaptureVector(block, Quaternions(2));

        Assert.AreEqual(1, block.GetSensorPacketCount(0));
        Assert.AreEqual(1, block.GetSensorPacketCount(1));
    }

    [TestMethod]
    public void ADeadUpstreamSensorStillReadsAsLive_WhileTheJoinerHoldsItsLastValue()
    {
        // KNOWN LIMITATION.
        //
        // Simulates exactly what the Joiner does when one node dies: it keeps republishing the
        // dead node's last value alongside the live one. From the rig's side the vector is the
        // same width every frame, so group 1 keeps "arriving".
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;

        for (int i = 0; i < 20; i++)
            BlockHarness.CaptureVector(block, Quaternions(2));

        int deadSensorCount = block.GetSensorPacketCount(1);

        Assert.AreEqual(20, deadSensorCount,
            "the counter keeps climbing for a node that has not transmitted since frame 1 — " +
            "the card will show it live");
    }

    [TestMethod]
    public void WhenTheJoinerDropsTheDeadSourceEntirely_TheCounterDoesStop()
    {
        // The one case the counter does catch: the joined vector actually gets shorter.
        //
        // This shortening used to be reachable in a real pipeline, and it re-mapped every later
        // sensor onto the wrong segment when it happened. The Joiner no longer emits a short
        // record — it withholds output until every declared input has spoken once, and after
        // that a quiet source stands in with its last value — so upstream of a Joiner this state
        // is now unreachable. It is driven directly here because the block itself still has to
        // behave sanely if some other source ever hands it a short vector.
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;

        BlockHarness.CaptureVector(block, Quaternions(2));
        int before = block.GetSensorPacketCount(1);

        for (int i = 0; i < 10; i++)
            BlockHarness.CaptureVector(block, Quaternions(1));   // only one group now

        Assert.AreEqual(before, block.GetSensorPacketCount(1),
            "with the group gone, the counter correctly stops");
        Assert.IsGreaterThan(before, block.GetSensorPacketCount(0),
            "the surviving group keeps counting — but it is now group 0, not group 1");
    }

    [TestMethod]
    public void SerialModeCountersAreNotAffectedByThisGap()
    {
        // On the serial path the block decodes one sensor id per physical frame, so the count
        // really is a per-sensor arrival count. The gap is specific to the joined upstream path.
        using var block = new BodyRigBlock();
        using var clock = new MOSAIC.Models.FlowControl.ClockBlock();

        block.ReceiveInput(clock, 0);

        Assert.AreEqual(0, block.GetSensorPacketCount(0),
            "no serial frames decoded, so nothing is counted");
    }
}
