using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.ViewModels.Devices;
using BodyRigBlock = MOSAIC.Models.Devices.BodyRig;

namespace MOSAIC.Tests.ViewModels.Devices;

/// <summary>
/// Tests for <see cref="BodyRigViewModel"/>, the segment inspector and chain strip behind the
/// BodyRig card.
/// </summary>
/// <remarks>
/// Two behaviours carry most of the weight here. First, every editable field writes through to
/// the block on change, so filling the fields from the block is inherently re-entrant — the
/// tests that prove a pure read (selecting a segment, rebuilding the strip) does not mutate the
/// calibration are the ones that matter. Second, the chain strip is derived purely from the
/// block's parent map, so it has to stay correct for roots, nested children, cycles and
/// unreachable segments.
/// </remarks>
[TestClass]
public class BodyRigViewModelTests
{
    private const float Tol = 1e-3f;

    // ── Construction ────────────────────────────────────────────────────────

    [TestMethod]
    public void Constructor_WithNoBlock_DoesNotThrow()
    {
        // The designer and the template selector can both hand us a null block.
        var vm = new BodyRigViewModel();

        Assert.IsNull(vm.Info);
        Assert.IsNull(vm.Block);
        Assert.IsEmpty(vm.Chain);
    }

    [TestMethod]
    public void Constructor_WithNoBlock_SurvivesFieldEditsAndRefresh()
    {
        var vm = new BodyRigViewModel();

        vm.SelectedSegment = 0;
        vm.SensorChoice = 2;
        vm.LinkX = 1f;
        vm.DhRoll = 15f;
        vm.SegmentName = "x";
        vm.RefreshLive();
        vm.ApplySegmentCountCommand.Execute(null);
        vm.ChainSequentiallyCommand.Execute(null);
        vm.ClearParentsCommand.Execute(null);
        vm.ToggleConnectionCommand.Execute(null);

        Assert.IsEmpty(vm.Chain);
    }

    [TestMethod]
    public void Constructor_AdoptsTheBlocksPortAndSegmentCount()
    {
        using var block = new BodyRigBlock { PortNumber = "COM12" };
        block.NumOfChannels = 6;

        var vm = new BodyRigViewModel(block);

        Assert.AreEqual("COM12", vm.PortName);
        Assert.AreEqual(6, vm.PendingSegmentCount);
        Assert.AreEqual(5, vm.MaxSegment);
        Assert.IsNotNull(vm.Info);
    }

    [TestMethod]
    public void PortName_IsAFreeTextName_NotJustADigit()
    {
        // The old card offered a digits-only box and rebuilt the port as $"COM{n}",
        // which destroyed any port name that was not exactly COM<n>.
        using var block = new BodyRigBlock { PortNumber = "/dev/ttyUSB0" };

        var vm = new BodyRigViewModel(block);

        Assert.AreEqual("/dev/ttyUSB0", vm.PortName);
    }

    [TestMethod]
    public void Constructor_PopulatesTheInspectorFromSegmentZero()
    {
        using var block = new BodyRigBlock();
        block.SetLinkLength(0, 0.4f, -0.1f, 0.2f);
        block.SetSensorIndex(0, 3);

        var vm = new BodyRigViewModel(block);

        Assert.AreEqual(40f, vm.LinkX, Tol);   // metres in, centimetres out
        Assert.AreEqual(-10f, vm.LinkY, Tol);
        Assert.AreEqual(20f, vm.LinkZ, Tol);
        Assert.AreEqual(4, vm.SensorChoice, "sensor 3 is choice index 4 (index 0 is the passive dash)");
    }

    // ── Chain strip ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Chain_HasOneRowPerSegment()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 5;

        var vm = new BodyRigViewModel(block);

        Assert.HasCount(5, vm.Chain);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, vm.Chain.Select(c => c.Index).ToArray());
    }

    [TestMethod]
    public void Chain_UnparentedSegments_AreAllRootsAtDepthZero()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 4;

        var vm = new BodyRigViewModel(block);

        Assert.IsTrue(vm.Chain.All(c => c.Depth == 0));
        Assert.IsTrue(vm.Chain.All(c => c.Glyph == "●"));
        Assert.AreEqual("4 chains", vm.ChainSummary);
    }

    [TestMethod]
    public void Chain_NestsChildrenUnderTheirParent()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 3;
        block.SetParent(1, 0);
        block.SetParent(2, 1);

        var vm = new BodyRigViewModel(block);

        // Depth-first from the root: 0, then 1 under it, then 2 under that.
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, vm.Chain.Select(c => c.Index).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, vm.Chain.Select(c => c.Depth).ToArray());
        Assert.AreEqual("1 chain", vm.ChainSummary);
    }

    [TestMethod]
    public void Chain_IndentIsCappedSoDeepChainsKeepTheirName()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 10;
        for (int i = 1; i < 10; i++) block.SetParent(i, i - 1);

        var vm = new BodyRigViewModel(block);

        Assert.AreEqual(9, vm.Chain[9].Depth);
        Assert.AreEqual(48d, vm.Chain[9].IndentWidth, "indent must stop growing or the name is squeezed out");
    }

    [TestMethod]
    public void Chain_ShowsTheSensorBoundToEachSegment()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 3;
        block.SetSensorIndex(0, 7);
        block.SetSensorIndex(1, -1);

        var vm = new BodyRigViewModel(block);

        Assert.AreEqual("S7", vm.Chain[0].SensorLabel);
        Assert.AreEqual("—", vm.Chain[1].SensorLabel, "an unbound segment must read as passive");
        Assert.Contains("Passive", vm.Chain[1].SensorTooltip);
    }

    [TestMethod]
    public void Chain_UsesTheSegmentNameWhenSet()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;
        block.GetSegment(0)!.Name = "rightForeArm";

        var vm = new BodyRigViewModel(block);

        Assert.AreEqual("rightForeArm", vm.Chain[0].DisplayName);
        Assert.AreEqual("segment 1", vm.Chain[1].DisplayName);
    }

    [TestMethod]
    public void Chain_SelectingARow_MarksItAndMovesTheEditors()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 4;
        block.SetLinkLength(2, 0.5f, 0, 0);

        var vm = new BodyRigViewModel(block);
        vm.SelectSegmentCommand.Execute(2);

        Assert.AreEqual(2, vm.SelectedSegment);
        Assert.IsTrue(vm.Chain.Single(c => c.Index == 2).IsSelected);
        Assert.AreEqual(1, vm.Chain.Count(c => c.IsSelected), "exactly one row is selected");
        Assert.AreEqual(50f, vm.LinkX, Tol);
    }

    [TestMethod]
    public void Chain_RebuildTerminatesOnACycleAndFlagsTheStrandedSegments()
    {
        // Parent/Child are public on BodySegment, so a cycle can be built directly. The strip
        // must still terminate and must not silently omit the segments it could not reach.
        using var block = new BodyRigBlock();
        block.NumOfChannels = 3;
        var a = block.GetSegment(0)!;
        var b = block.GetSegment(1)!;
        a.Parent = b;
        b.Parent = a;

        var vm = new BodyRigViewModel(block);

        Assert.HasCount(3, vm.Chain, "every segment must appear exactly once");
        Assert.IsTrue(vm.Chain.Any(c => c.Glyph == "⚠"), "unreachable segments must be flagged");
        Assert.Contains("loose", vm.ChainSummary);
    }

    // ── Reading must not write ──────────────────────────────────────────────

    [TestMethod]
    public void SelectingASegment_DoesNotAlterItsDhAngles()
    {
        // Populating the fields fires the same change handlers that write back to the block.
        // Without the write-back guard, merely looking at a segment rotated it.
        using var block = new BodyRigBlock();
        block.SetDhAngles(1, 10f, -20f, 30f);
        var expected = block.GetDhAngles(1);

        var vm = new BodyRigViewModel(block) { SelectedSegment = 1 };

        var actual = block.GetDhAngles(1);
        Assert.AreEqual(expected.X, actual.X, 1e-5f, "roll");
        Assert.AreEqual(expected.Y, actual.Y, 1e-5f, "pitch");
        Assert.AreEqual(expected.Z, actual.Z, 1e-5f, "yaw");
    }

    [TestMethod]
    public void SelectingASegment_DoesNotAlterItsLinkLength()
    {
        using var block = new BodyRigBlock();
        block.SetLinkLength(2, 0.11f, 0.22f, 0.33f);

        var vm = new BodyRigViewModel(block) { SelectedSegment = 2 };

        var link = block.GetLinkLengths(2);
        Assert.AreEqual(0.11f, link.X, 1e-5f);
        Assert.AreEqual(0.22f, link.Y, 1e-5f);
        Assert.AreEqual(0.33f, link.Z, 1e-5f);
    }

    [TestMethod]
    public void SelectingAPassiveSegment_DoesNotBindItToSensorZero()
    {
        // The old numeric spinner had Minimum="0", so selecting an unbound segment clamped
        // -1 up to 0 and wrote that into the block.
        using var block = new BodyRigBlock();
        block.SetSensorIndex(3, -1);

        var vm = new BodyRigViewModel(block) { SelectedSegment = 3 };

        Assert.AreEqual(-1, block.GetSensorIndex(3), "a passive segment must stay passive");
        Assert.AreEqual(0, vm.SensorChoice, "choice 0 is the passive dash");
    }

    [TestMethod]
    public void SelectingASegment_DoesNotReparentIt()
    {
        using var block = new BodyRigBlock();
        block.SetParent(3, 1);

        var vm = new BodyRigViewModel(block) { SelectedSegment = 3 };

        Assert.AreEqual(1, block.GetParentIndex(3));
        Assert.AreEqual(1, vm.SelectedParentOption!.Index);
    }

    // ── Editing must write ──────────────────────────────────────────────────

    [TestMethod]
    public void EditingLinkLength_WritesCentimetresBackAsMetres()
    {
        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block) { SelectedSegment = 1 };

        vm.LinkX = 25f;
        vm.LinkY = -5f;

        var link = block.GetLinkLengths(1);
        Assert.AreEqual(0.25f, link.X, 1e-5f);
        Assert.AreEqual(-0.05f, link.Y, 1e-5f);
    }

    [TestMethod]
    public void EditingDhAngles_WritesThemBackInRollPitchYawOrder()
    {
        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block) { SelectedSegment = 1 };

        vm.DhRoll = 10f;
        vm.DhPitch = -20f;
        vm.DhYaw = 30f;

        const float rad2deg = 180f / MathF.PI;
        var dh = block.GetDhAngles(1);
        Assert.AreEqual(10f, dh.X * rad2deg, 1e-2f, "roll");
        Assert.AreEqual(-20f, dh.Y * rad2deg, 1e-2f, "pitch");
        Assert.AreEqual(30f, dh.Z * rad2deg, 1e-2f, "yaw");
    }

    [TestMethod]
    public void EditingSensorChoice_WritesThroughAndUpdatesTheStrip()
    {
        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block) { SelectedSegment = 2 };

        vm.SensorChoice = 8;   // "S7"

        Assert.AreEqual(7, block.GetSensorIndex(2));
        Assert.AreEqual("S7", vm.Chain.Single(c => c.Index == 2).SensorLabel);
    }

    [TestMethod]
    public void SelectingThePassiveChoice_UnbindsTheSegment()
    {
        using var block = new BodyRigBlock();
        block.SetSensorIndex(2, 5);
        var vm = new BodyRigViewModel(block) { SelectedSegment = 2 };

        vm.SensorChoice = 0;

        Assert.AreEqual(-1, block.GetSensorIndex(2));
    }

    [TestMethod]
    public void EditingSegmentName_WritesThroughAndUpdatesTheStrip()
    {
        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block) { SelectedSegment = 1 };

        vm.SegmentName = "leftHand";

        Assert.AreEqual("leftHand", block.GetSegment(1)!.Name);
        Assert.AreEqual("leftHand", vm.Chain.Single(c => c.Index == 1).DisplayName);
    }

    // ── Parenting through named options ─────────────────────────────────────

    [TestMethod]
    public void ParentOptions_OfferRootAndEveryOtherSegmentByName()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 3;
        block.GetSegment(0)!.Name = "Torso";

        var vm = new BodyRigViewModel(block) { SelectedSegment = 2 };

        Assert.AreEqual(-1, vm.ParentOptions[0].Index);
        Assert.Contains("root", vm.ParentOptions[0].Label);
        Assert.Contains("Torso", string.Join("|", vm.ParentOptions.Select(o => o.Label)));
        Assert.IsFalse(vm.ParentOptions.Any(o => o.Index == 2), "a segment cannot parent itself");
    }

    [TestMethod]
    public void ChoosingAParent_ReparentsAndRedrawsTheStrip()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 3;
        var vm = new BodyRigViewModel(block) { SelectedSegment = 2 };

        vm.SelectedParentOption = vm.ParentOptions.Single(o => o.Index == 0);

        Assert.AreEqual(0, block.GetParentIndex(2));
        Assert.AreEqual(1, vm.Chain.Single(c => c.Index == 2).Depth);
    }

    [TestMethod]
    public void ChoosingAParentThatWouldCycle_IsRejectedAndExplained()
    {
        // SetParent rejects cycles silently; the card has to say why nothing happened.
        using var block = new BodyRigBlock();
        block.NumOfChannels = 3;
        block.SetParent(1, 0);

        var vm = new BodyRigViewModel(block) { SelectedSegment = 0 };
        vm.SelectedParentOption = vm.ParentOptions.Single(o => o.Index == 1);

        Assert.AreEqual(-1, block.GetParentIndex(0), "the cycle must not be created");
        Assert.IsTrue(vm.HasTopologyWarning);
        Assert.Contains("cycle", vm.TopologyWarning);
    }

    // ── Chain setup ─────────────────────────────────────────────────────────

    [TestMethod]
    public void SegmentCount_IsStagedUntilRebuildIsPressed()
    {
        // Bound live, typing "10" over "1" would run the destructive rebuild twice.
        using var block = new BodyRigBlock();
        block.NumOfChannels = 4;
        var vm = new BodyRigViewModel(block);

        vm.PendingSegmentCount = 7;

        Assert.AreEqual(4, block.SegmentCount, "nothing happens until Rebuild");
        Assert.IsTrue(vm.SegmentCountDirty);

        vm.ApplySegmentCountCommand.Execute(null);

        Assert.AreEqual(7, block.SegmentCount);
        Assert.IsFalse(vm.SegmentCountDirty);
        Assert.HasCount(7, vm.Chain);
    }

    [TestMethod]
    public void Rebuild_ClampsTheSelectionIntoTheNewRange()
    {
        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block) { SelectedSegment = 8 };

        vm.PendingSegmentCount = 3;
        vm.ApplySegmentCountCommand.Execute(null);

        Assert.AreEqual(2, vm.SelectedSegment);
        Assert.AreEqual(2, vm.MaxSegment);
    }

    [TestMethod]
    public void ChainSequentially_LinksEverySegmentHeadToTail()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 5;
        var vm = new BodyRigViewModel(block);

        vm.ChainSequentiallyCommand.Execute(null);

        for (int i = 1; i < 5; i++) Assert.AreEqual(i - 1, block.GetParentIndex(i), $"segment {i}");
        Assert.AreEqual("1 chain", vm.ChainSummary);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, vm.Chain.Select(c => c.Depth).ToArray());
    }

    [TestMethod]
    public void ClearParents_DetachesEverything()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 4;
        var vm = new BodyRigViewModel(block);
        vm.ChainSequentiallyCommand.Execute(null);

        vm.ClearParentsCommand.Execute(null);

        for (int i = 0; i < 4; i++) Assert.AreEqual(-1, block.GetParentIndex(i), $"segment {i}");
        Assert.AreEqual("4 chains", vm.ChainSummary);
    }

    // ── Live refresh ────────────────────────────────────────────────────────

    [TestMethod]
    public void RefreshLive_ReportsIdleBeforeAnyDataArrives()
    {
        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block);

        vm.RefreshLive();

        Assert.AreEqual("IDLE", vm.ModeLabel);
        Assert.AreEqual("Idle", vm.HealthText);
        Assert.IsFalse(vm.IsReceiving);
        Assert.AreEqual(0, vm.SensorsArriving);
    }

    [TestMethod]
    public void RefreshLive_CountsBoundSensorsAndReportsThemDarkWhenSilent()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 3;
        block.SetSensorIndex(0, 0);
        block.SetSensorIndex(1, 1);
        block.SetSensorIndex(2, -1);

        var vm = new BodyRigViewModel(block);
        vm.RefreshLive();

        Assert.AreEqual(2, vm.BoundSensorCount, "the passive segment is not a bound sensor");
        Assert.AreEqual(2, vm.BoundSensorsDark, "nothing is arriving, so both are dark");
        Assert.Contains("2 bound", vm.SensorCoverage);
    }

    [TestMethod]
    public void RefreshLive_MirrorsTheParserErrorCounter()
    {
        // Info.Flag is overwritten on every received chunk, so the card mirrors the counter.
        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block);

        block.Info.FrameErrors = 4;
        vm.RefreshLive();

        Assert.AreEqual(4, vm.FrameErrors);
        Assert.IsTrue(vm.HasFrameErrors);
    }

    [TestMethod]
    public void RefreshLive_DoesNotRebuildTheStripWhenNothingStructuralChanged()
    {
        // The ItemsControl must not re-template at tick rate.
        using var block = new BodyRigBlock();
        block.NumOfChannels = 3;
        var vm = new BodyRigViewModel(block);
        var first = vm.Chain[0];

        vm.RefreshLive();
        vm.RefreshLive();

        Assert.AreSame(first, vm.Chain[0], "rows must be mutated in place, not recreated");
    }

    [TestMethod]
    public void SanityHint_CallsOutTheUntouchedDefaultLinkLength()
    {
        // A fresh block is ten 1-metre rods, which is why the pose plot looks absurd.
        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block);

        vm.RefreshLive();

        Assert.Contains("default", vm.SanityHint);

        vm.LinkX = 20f;
        vm.RefreshLive();

        Assert.AreEqual(string.Empty, vm.SanityHint);
    }

    [TestMethod]
    public void JointFrameLabel_DistinguishesARootFromAChild()
    {
        using var block = new BodyRigBlock();
        block.NumOfChannels = 2;
        block.GetSegment(0)!.Name = "Torso";
        block.SetParent(1, 0);

        var vm = new BodyRigViewModel(block);

        vm.SelectedSegment = 0;
        vm.RefreshLive();
        Assert.Contains("world frame", vm.JointFrameLabel);

        vm.SelectedSegment = 1;
        vm.RefreshLive();
        Assert.Contains("Torso", vm.JointFrameLabel);
    }

    // ── Connection ──────────────────────────────────────────────────────────

    [TestMethod]
    public void ToggleConnection_OnAnUnavailablePort_DoesNotClaimToBeOpen()
    {
        // Connect() reports failure through Info.Flag rather than throwing, so the card must
        // read the port itself instead of assuming success.
        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block) { PortName = "COM250" };

        vm.ToggleConnectionCommand.Execute(null);

        Assert.AreEqual("COM250", block.PortNumber);
        Assert.IsFalse(vm.IsPortOpen);
        Assert.IsFalse(block.IsPortOpen);
    }
}
