using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.BodyRig;
using MOSAIC.ViewModels.Devices;
using BodyRigBlock = MOSAIC.Models.Devices.BodyRig;

namespace MOSAIC.Tests.Models.Devices;

/// <summary>
/// Covers kinematic chains that branch — a torso with more than one limb hanging off it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BodySegment"/> held a single <c>Child</c> until the lab's own calibration models
/// showed why that could not stand: <c>double_hand</c> parents two arms to one torso and
/// <c>bimanual</c> parents three branches. Attaching the second child evicted the first, so those
/// files loaded as one intact chain plus limbs re-rooted at the world origin, and
/// <c>LoadCalibration</c> reported success either way.
/// </para>
/// <para>
/// The records below are copies of
/// <c>MOSAIC/Assets/BodyRigCalibrationFiles/{double_hand,bimanual}</c>, inlined so these tests
/// do not depend on the build having deployed that folder next to the test runner.
/// </para>
/// </remarks>
[TestClass]
public class BodyRigBranchingTests
{
    private const float Tol = 1e-4f;

    /// <summary>Torso root (3) with two symmetric 3-segment arms.</summary>
    private const string DoubleHand =
        "0:0:0:1:0:1.231816E-07:0:1:0\n" +
        "0.3:0:0:-8.510921E-15:-4.371139E-08:-1:1.947072E-07:2:1\n" +
        "0.3:0:0:1.219952E-07:-4.047187E-08:-0.9996573:0.02617711:3:2\n" +
        "0.38:0:0.38:-3.090862E-08:-3.090862E-08:0.7071068:-0.7071068:-1:3\n" +
        "0.3:0:0:-8.742278E-08:4.371139E-08:4.371138E-08:1:3:4\n" +
        "0.3:0:0:1:1.231816E-07:8.742276E-08:8.742276E-08:4:5\n" +
        "0:0:0:1.910685E-15:-4.371139E-08:-1:-4.371139E-08:5:6\n";

    /// <summary>Same root, three branches, and two segments sharing one sensor slot.</summary>
    private const string Bimanual =
        "0:0:0:-4.371139E-08:0:0:1:1:0\n" +
        "0.18:0:0:-8.510921E-15:-4.371139E-08:-1:1.947072E-07:2:1\n" +
        "0.28:0:0:1.219952E-07:-4.047187E-08:-0.9996573:0.02617711:3:2\n" +
        "0:0:0:-3.090862E-08:-3.090862E-08:0.7071068:-0.7071068:-1:3\n" +
        "0:0:0:1:0:0:0:3:4\n" +
        "0.19:0:0:0.3007059:-1.685245E-07:-0.9537169:7.302919E-09:3:4\n" +
        "0.01:0:0:1.910685E-15:1:-4.371139E-08:-4.371139E-08:5:6\n";

    private string WriteModel(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "model-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content);
        return path;
    }

    private BodyRigBlock LoadRig(string content, int segments = 7)
    {
        var path = WriteModel(content);
        try
        {
            var rig = new BodyRigBlock();
            rig.NumOfChannels = segments;
            rig.LoadCalibration(path);
            return rig;
        }
        finally { File.Delete(path); }
    }

    /// <summary>Indices of a segment's children, by position in the chain array.</summary>
    private static int[] ChildIndices(BodyRigBlock rig, int index)
    {
        var parent = rig.GetSegment(index)!;
        return Enumerable.Range(0, rig.SegmentCount)
            .Where(i => parent.Children.Any(c => ReferenceEquals(c, rig.GetSegment(i))))
            .ToArray();
    }

    // ── The model ───────────────────────────────────────────────────────────

    [TestMethod]
    public void AParentKeepsEveryChildAttachedToIt()
    {
        using var rig = new BodyRigBlock();

        rig.SetParent(1, 0);
        rig.SetParent(2, 0);

        CollectionAssert.AreEquivalent(new[] { 1, 2 }, ChildIndices(rig, 0));
        Assert.AreEqual(0, rig.GetParentIndex(1), "the first child is not evicted");
        Assert.AreEqual(0, rig.GetParentIndex(2));
    }

    [TestMethod]
    public void Reparenting_RemovesTheSegmentFromItsOldParentOnly()
    {
        using var rig = new BodyRigBlock();
        rig.SetParent(1, 0);
        rig.SetParent(2, 0);

        rig.SetParent(2, 1);

        CollectionAssert.AreEquivalent(new[] { 1 }, ChildIndices(rig, 0), "2 left, 1 stayed");
        CollectionAssert.AreEquivalent(new[] { 2 }, ChildIndices(rig, 1));
    }

    [TestMethod]
    public void CyclesAreStillRejected_AcrossABranch()
    {
        // The guard has to walk a tree now, not a list: 2 is reachable from 0 only via 1.
        using var rig = new BodyRigBlock();
        rig.SetParent(1, 0);
        rig.SetParent(2, 1);
        rig.SetParent(3, 0);

        rig.SetParent(0, 2);   // would close a loop

        Assert.AreEqual(-1, rig.GetParentIndex(0), "rejected");
    }

    [TestMethod]
    public void ForwardKinematics_ReachesEveryBranch()
    {
        var root = new BodySegment { LinkLength = new Vector3F(1, 0, 0) };
        var armA = new BodySegment { LinkLength = new Vector3F(1, 0, 0) };
        var armB = new BodySegment { LinkLength = new Vector3F(0, 0, 2) };
        foreach (var s in new[] { root, armA, armB }) s.SetDh(0, 0, 0);

        root.AddChild(armA);
        root.AddChild(armB);
        armA.Parent = root;
        armB.Parent = root;

        // Only the root is driven; both branches must be recomputed off its new tip.
        root.UpdateOrientation(QuaternionF.FromEulerDegrees(0, 0, 90));

        Assert.AreEqual(0f, root.Position.X, Tol);
        Assert.AreEqual(1f, root.Position.Y, Tol);

        Assert.AreEqual(1f, armA.Position.X, Tol, "branch A hangs off the root tip");
        Assert.AreEqual(1f, armA.Position.Y, Tol);

        Assert.AreEqual(0f, armB.Position.X, Tol, "branch B too, not just the first one");
        Assert.AreEqual(1f, armB.Position.Y, Tol);
        Assert.AreEqual(2f, armB.Position.Z, Tol);
    }

    // ── The lab's real models ───────────────────────────────────────────────

    [TestMethod]
    public void DoubleHand_LoadsAsOneTorsoWithTwoArms()
    {
        using var rig = LoadRig(DoubleHand);

        // Declared in the file as parents 1,2,3,-1,3,4,5.
        Assert.AreEqual(-1, rig.GetParentIndex(3), "segment 3 is the torso root");
        CollectionAssert.AreEquivalent(new[] { 2, 4 }, ChildIndices(rig, 3),
            "both arms hang off the torso — this is what used to be severed");

        Assert.AreEqual(1, rig.GetParentIndex(0));
        Assert.AreEqual(2, rig.GetParentIndex(1));
        Assert.AreEqual(3, rig.GetParentIndex(2));
        Assert.AreEqual(3, rig.GetParentIndex(4));
        Assert.AreEqual(4, rig.GetParentIndex(5));
        Assert.AreEqual(5, rig.GetParentIndex(6));
    }

    [TestMethod]
    public void DoubleHand_KeepsItsGeometryAndSensorBindings()
    {
        using var rig = LoadRig(DoubleHand);

        var torso = rig.GetLinkLengths(3);
        Assert.AreEqual(0.38f, torso.X, Tol);
        Assert.AreEqual(0f, torso.Y, Tol);
        Assert.AreEqual(0.38f, torso.Z, Tol);

        Assert.AreEqual(0.30f, rig.GetLinkLengths(2).X, Tol, "upper arm");
        Assert.AreEqual(0.30f, rig.GetLinkLengths(1).X, Tol, "forearm");
        Assert.AreEqual(0f, rig.GetLinkLengths(0).X, Tol, "hand");

        for (int i = 0; i < 7; i++)
            Assert.AreEqual(i, rig.GetSensorIndex(i), $"segment {i} drives off slot {i}");
    }

    [TestMethod]
    public void Bimanual_LoadsAllThreeBranches()
    {
        using var rig = LoadRig(Bimanual);

        Assert.AreEqual(-1, rig.GetParentIndex(3));
        CollectionAssert.AreEquivalent(new[] { 2, 4, 5 }, ChildIndices(rig, 3),
            "three branches; the single-child model kept only the last");

        Assert.AreEqual(5, rig.GetParentIndex(6));
        Assert.AreEqual(0.28f, rig.GetLinkLengths(2).X, Tol);
        Assert.AreEqual(0.18f, rig.GetLinkLengths(1).X, Tol);
        Assert.AreEqual(0.19f, rig.GetLinkLengths(5).X, Tol);
    }

    [TestMethod]
    public void Bimanual_LetsTwoSegmentsShareOneSensorSlot()
    {
        using var rig = LoadRig(Bimanual);

        Assert.AreEqual(4, rig.GetSensorIndex(4));
        Assert.AreEqual(4, rig.GetSensorIndex(5), "deliberate — one IMU drives two segments");
        Assert.IsFalse(Enumerable.Range(0, 7).Any(i => rig.GetSensorIndex(i) == 5),
            "slot 5 goes unused in this model");
    }

    // ── What the card shows ─────────────────────────────────────────────────

    [TestMethod]
    public void TheChainStrip_DrawsDoubleHandAsASingleTreeWithNothingLoose()
    {
        using var rig = LoadRig(DoubleHand);

        var vm = new BodyRigViewModel(rig);

        Assert.AreEqual("1 chain", vm.ChainSummary, "one root, no orphans");
        Assert.HasCount(7, vm.Chain);

        // Depth-first from the torso: 3, then the 2-1-0 arm, then the 4-5-6 arm.
        CollectionAssert.AreEqual(new[] { 3, 2, 1, 0, 4, 5, 6 }, vm.Chain.Select(c => c.Index).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 1, 2, 3 }, vm.Chain.Select(c => c.Depth).ToArray());
        Assert.AreEqual(1, vm.Chain.Count(c => c.Glyph == "●"), "exactly one root glyph");
    }

    [TestMethod]
    public void TheChainStrip_DrawsBimanualsThreeBranches()
    {
        using var rig = LoadRig(Bimanual);

        var vm = new BodyRigViewModel(rig);

        Assert.AreEqual("1 chain", vm.ChainSummary);
        CollectionAssert.AreEqual(new[] { 3, 2, 1, 0, 4, 5, 6 }, vm.Chain.Select(c => c.Index).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 1, 1, 2 }, vm.Chain.Select(c => c.Depth).ToArray());
    }
}
