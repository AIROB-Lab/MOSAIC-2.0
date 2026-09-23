using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.BodyRig;

namespace MOSAIC.Tests.Components.Devices.BodyRig;

/// <summary>
/// Numerical-stability tests for the DH mapping and the parent → child propagation.
/// </summary>
/// <remarks>
/// <para>
/// The propagation in <see cref="BodySegment.UpdateOrientation(QuaternionF)"/> hands a child
/// <c>O · D̄</c>, and the child immediately recomputes <c>(O · D̄) · D</c>. Since
/// <c>D̄ · D = |D|²</c> and not 1, the round trip is only an identity while <c>D</c> is unit
/// length — otherwise it is a gain applied on <em>every single parent update</em>.
/// </para>
/// <para>
/// That matters because a real rig runs at a couple of hundred hertz: a DH quaternion of
/// |D| = 0.975 drives a limb to the origin inside a second, and |D| &gt; 1 overflows to infinity,
/// at which point <c>BodyRig.UpdateSampleBuffer</c>'s finiteness guard silently keeps
/// republishing stale values while the frame counter climbs — the rig reads as healthy and is
/// frozen. Non-unit values are reachable: <c>LoadCalibration</c> parses four raw floats from a
/// text file, and <c>SetDhQuaternion</c> is public.
/// </para>
/// </remarks>
[TestClass]
public class BodySegmentStabilityTests
{
    private const float Tol = 1e-5f;

    private static float Norm(QuaternionF q) => q.Magnitude();

    // ── DH normalisation ────────────────────────────────────────────────────

    [TestMethod]
    public void SetDh_FromQuaternion_NormalisesANonUnitInput()
    {
        var s = new BodySegment();

        s.SetDh(0.9f, 0.1f, 0.2f, 0.3f);   // |q| = 0.9747

        Assert.AreEqual(1f, Norm(s.DhRelativeToGyro), Tol);
    }

    [TestMethod]
    public void SetDh_FromQuaternion_PreservesTheRotationItRepresents()
    {
        // Normalising must not change the orientation, only its scale.
        var s = new BodySegment();
        var unit = QuaternionF.FromEulerDegrees(20, -35, 60);

        s.SetDh(unit.W * 3f, unit.X * 3f, unit.Y * 3f, unit.Z * 3f);

        Assert.AreEqual(unit.X, s.DhRelativeToGyro.X, Tol);
        Assert.AreEqual(unit.Y, s.DhRelativeToGyro.Y, Tol);
        Assert.AreEqual(unit.Z, s.DhRelativeToGyro.Z, Tol);
        Assert.AreEqual(unit.W, s.DhRelativeToGyro.W, Tol);
    }

    [TestMethod]
    public void SetDh_FromAngles_IsAlreadyUnit()
    {
        var s = new BodySegment();

        s.SetDh(15f, -25f, 40f);

        Assert.AreEqual(1f, Norm(s.DhRelativeToGyro), Tol);
    }

    [TestMethod]
    public void Calibrate_ProducesAUnitDhMapping()
    {
        var s = new BodySegment();
        s.SetCalibrationPose(QuaternionF.FromEulerDegrees(0, 0, 90));

        s.Calibrate(QuaternionF.FromEulerDegrees(35, 20, -15));

        Assert.AreEqual(1f, Norm(s.DhRelativeToGyro), Tol);
    }

    // ── Propagation stability ───────────────────────────────────────────────

    [TestMethod]
    public void RepeatedParentUpdates_DoNotDecayTheChildQuaternion()
    {
        // The regression this guards: with a non-unit DH the child's norm was multiplied by
        // |D|² per parent update. 400 updates is under two seconds of real streaming.
        var root = new BodySegment { LinkLength = new Vector3F(0.3f, 0, 0) };
        root.SetDh(0, 0, 0);

        var child = new BodySegment { Parent = root, LinkLength = new Vector3F(0.3f, 0, 0) };
        child.SetDh(0.9f, 0.1f, 0.2f, 0.3f);   // deliberately non-unit on the way in
        root.AddChild(child);

        child.UpdateOrientation(QuaternionF.FromEulerDegrees(10, 0, 0));

        for (int i = 0; i < 400; i++)
            root.UpdateOrientation(QuaternionF.FromEulerDegrees(0, 0, i % 90));

        Assert.AreEqual(1f, Norm(child.Orientation), 1e-3f,
            "the child's orientation must stay unit length across sustained streaming");
        Assert.IsTrue(float.IsFinite(child.Position.X), "position must stay finite");
        Assert.IsTrue(float.IsFinite(child.Position.Y));
        Assert.IsTrue(float.IsFinite(child.Position.Z));
    }

    [TestMethod]
    public void ChildPositionStaysAttachedToTheParentTipAcrossManyUpdates()
    {
        // The propagation exists to refresh Position; prove it still does that, and that the
        // child does not drift away from the parent's distal end.
        var root = new BodySegment { LinkLength = new Vector3F(1, 0, 0) };
        root.SetDh(0, 0, 0);

        var child = new BodySegment { Parent = root, LinkLength = new Vector3F(1, 0, 0) };
        child.SetDh(0, 0, 0);
        root.AddChild(child);

        child.UpdateOrientation(QuaternionF.Identity);

        for (int i = 0; i < 200; i++)
            root.UpdateOrientation(QuaternionF.Identity);

        var offset = child.Position - root.Position;
        Assert.AreEqual(1f, offset.Magnitude(), 1e-3f,
            "the child tip must stay one link length from the parent tip");
    }

    [TestMethod]
    public void AZeroDhQuaternion_DoesNotZeroTheSegment()
    {
        // A malformed calibration line of 0:0:0:0 previously wiped the segment outright.
        var s = new BodySegment();

        s.SetDh(0f, 0f, 0f, 0f);

        Assert.AreEqual(1f, Norm(s.DhRelativeToGyro), Tol,
            "a degenerate DH must fall back to a usable rotation, not annihilate the segment");
    }

    // ── Documented limitation ───────────────────────────────────────────────

    [TestMethod]
    public void ParentUpdate_DoesNotRotateAnUnsensoredChild()
    {
        // Documents a REAL LIMITATION, not desired behaviour.
        //
        // The propagation passes the child O·D̄ and the child recomputes (O·D̄)·D = O·|D|².
        // With a unit D that is exactly O — so a parent's rotation never reaches its child's
        // orientation. A segment with no sensor of its own therefore stays pinned in the WORLD
        // frame while the body turns, instead of following its parent rigidly.
        //
        // This is why virtual/passive joints (clavicle, spine link, sensorless pelvis root)
        // cannot currently be modelled, and it is one of the defects the model rethink targets.
        var root = new BodySegment { LinkLength = new Vector3F(1, 0, 0) };
        root.SetDh(0, 0, 0);

        var child = new BodySegment { Parent = root, LinkLength = new Vector3F(1, 0, 0) };
        child.SetDh(0, 0, 0);
        root.AddChild(child);

        child.UpdateOrientation(QuaternionF.Identity);
        var before = child.Orientation;

        // Yaw the parent a long way. A rigid body would carry the child round with it.
        root.UpdateOrientation(QuaternionF.FromEulerDegrees(0, 0, 90));

        Assert.AreEqual(before.X, child.Orientation.X, Tol);
        Assert.AreEqual(before.Y, child.Orientation.Y, Tol);
        Assert.AreEqual(before.Z, child.Orientation.Z, Tol);
        Assert.AreEqual(before.W, child.Orientation.W, Tol);
    }
}
