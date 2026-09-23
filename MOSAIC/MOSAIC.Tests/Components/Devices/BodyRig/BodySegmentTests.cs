using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.BodyRig;

namespace MOSAIC.Tests.Components.Devices.BodyRig;

/// <summary>
/// Tests for <see cref="BodySegment"/> — the forward-kinematics node of the BodyRig chain:
/// DH configuration, calibration, orientation/position propagation, and the topology helpers
/// that <c>BodyRig.SetParent</c> relies on to reject cycles.
/// </summary>
[TestClass]
public class BodySegmentTests
{
    private const float Tol = 1e-4f;
    private const float Rad2Deg = 180f / MathF.PI;

    private static void AssertVector(Vector3F expected, Vector3F actual, float tol = Tol)
    {
        Assert.AreEqual(expected.X, actual.X, tol, "X");
        Assert.AreEqual(expected.Y, actual.Y, tol, "Y");
        Assert.AreEqual(expected.Z, actual.Z, tol, "Z");
    }

    private static void AssertQuaternion(QuaternionF expected, QuaternionF actual, float tol = Tol)
    {
        Assert.AreEqual(expected.X, actual.X, tol, "X");
        Assert.AreEqual(expected.Y, actual.Y, tol, "Y");
        Assert.AreEqual(expected.Z, actual.Z, tol, "Z");
        Assert.AreEqual(expected.W, actual.W, tol, "W");
    }

    // ── Construction ────────────────────────────────────────────────────────

    [TestMethod]
    public void DefaultConstructor_UsesUnitXLink_AndMinus90YawDh()
    {
        var s = new BodySegment();

        AssertVector(new Vector3F(1, 0, 0), s.LinkLength);
        AssertVector(new Vector3F(1, 0, 0), s.Position);
        AssertVector(s.Position, s.LocalPosition);
        AssertQuaternion(QuaternionF.FromEulerDegrees(0, 0, -90), s.DhRelativeToGyro);
    }

    [TestMethod]
    public void DefaultConstructor_StartsUnparented_WithIdentityOrientation()
    {
        var s = new BodySegment();

        Assert.IsNull(s.Parent);
        Assert.IsEmpty(s.Children);
        Assert.AreEqual(QuaternionF.Identity, s.Orientation);
        Assert.AreEqual(QuaternionF.Identity, s.LocalOrientation);
        Assert.AreEqual(-1, s.SensorIndex);
        Assert.AreEqual(-1, s.OrdinalIndex);
    }

    [TestMethod]
    public void ExplicitConstructor_AppliesRollPitchYawAndLink()
    {
        var s = new BodySegment(0, 0, 45, 0.3f, 0.1f, -0.2f);

        AssertVector(new Vector3F(0.3f, 0.1f, -0.2f), s.LinkLength);
        AssertQuaternion(QuaternionF.FromEulerDegrees(0, 0, 45), s.DhRelativeToGyro);
    }

    // ── DH configuration ────────────────────────────────────────────────────

    [TestMethod]
    public void SetDh_FromAngles_RoundTripsThroughToEulerAngles()
    {
        var s = new BodySegment();

        s.SetDh(15f, -25f, 40f);

        var e = s.DhRelativeToGyro.ToEulerAngles();
        Assert.AreEqual(15f, e.X * Rad2Deg, 1e-2f, "roll");
        Assert.AreEqual(-25f, e.Y * Rad2Deg, 1e-2f, "pitch");
        Assert.AreEqual(40f, e.Z * Rad2Deg, 1e-2f, "yaw");
    }

    [TestMethod]
    public void SetDh_FromQuaternionComponents_UsesWxyzOrder()
    {
        var s = new BodySegment();

        s.SetDh(0.5f, 0.5f, 0.5f, 0.5f); // w, x, y, z

        AssertQuaternion(new QuaternionF(0.5f, 0.5f, 0.5f, 0.5f), s.DhRelativeToGyro);
    }

    // ── Calibration ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Calibrate_MakesTheCalibrationPoseTheReportedOrientation()
    {
        // Calibrating against a sensor reading must make that exact reading map to the
        // configured pose — that is the whole contract of the calibration step.
        var s = new BodySegment();
        var pose = QuaternionF.FromEulerDegrees(0, 0, 90);
        var sensorAtCalibration = QuaternionF.FromEulerDegrees(35, 20, -15);

        s.SetCalibrationPose(pose);
        s.Calibrate(sensorAtCalibration);
        s.UpdateOrientation(sensorAtCalibration);

        AssertQuaternion(pose, s.Orientation, 1e-3f);
    }

    [TestMethod]
    public void Calibrate_FromWxyzArray_MatchesQuaternionOverload()
    {
        var q = QuaternionF.FromEulerDegrees(11, 22, 33);

        var viaArray = new BodySegment();
        viaArray.Calibrate(new[] { q.W, q.X, q.Y, q.Z });

        var viaQuaternion = new BodySegment();
        viaQuaternion.Calibrate(q);

        AssertQuaternion(viaQuaternion.DhRelativeToGyro, viaArray.DhRelativeToGyro);
    }

    [TestMethod]
    public void Calibrate_FromComponents_UsesXyzwOrder()
    {
        var q = QuaternionF.FromEulerDegrees(11, 22, 33);

        var viaComponents = new BodySegment();
        viaComponents.Calibrate(q.X, q.Y, q.Z, q.W);

        var viaQuaternion = new BodySegment();
        viaQuaternion.Calibrate(q);

        AssertQuaternion(viaQuaternion.DhRelativeToGyro, viaComponents.DhRelativeToGyro);
    }

    // ── Forward kinematics: root ────────────────────────────────────────────

    [TestMethod]
    public void UpdateOrientation_OnRoot_PlacesTipAtRotatedLink()
    {
        var s = new BodySegment { LinkLength = new Vector3F(2, 0, 0) };
        s.SetDh(0, 0, 0); // no DH offset, so orientation == the sensor reading

        s.UpdateOrientation(QuaternionF.FromEulerDegrees(0, 0, 90));

        AssertVector(new Vector3F(0, 2, 0), s.Position);
        AssertVector(s.Position, s.LocalPosition);
        AssertQuaternion(s.Orientation, s.LocalOrientation);
    }

    [TestMethod]
    public void UpdateOrientation_AppliesDhOffsetAfterTheSensorReading()
    {
        var s = new BodySegment { LinkLength = new Vector3F(1, 0, 0) };
        s.SetDh(0, 0, 90); // +90° yaw baked into the DH frame

        s.UpdateOrientation(QuaternionF.Identity);

        AssertQuaternion(QuaternionF.FromEulerDegrees(0, 0, 90), s.Orientation);
        AssertVector(new Vector3F(0, 1, 0), s.Position);
    }

    [TestMethod]
    public void UpdateOrientation_FromWxyzArray_MatchesQuaternionOverload()
    {
        var q = QuaternionF.FromEulerDegrees(12, 34, 56);

        var viaArray = new BodySegment();
        viaArray.UpdateOrientation(new[] { q.W, q.X, q.Y, q.Z });

        var viaQuaternion = new BodySegment();
        viaQuaternion.UpdateOrientation(q);

        AssertQuaternion(viaQuaternion.Orientation, viaArray.Orientation);
        AssertVector(viaQuaternion.Position, viaArray.Position);
    }

    [TestMethod]
    public void UpdateOrientation_FromComponents_UsesXyzwOrder()
    {
        var q = QuaternionF.FromEulerDegrees(12, 34, 56);

        var viaComponents = new BodySegment();
        viaComponents.UpdateOrientation(q.X, q.Y, q.Z, q.W);

        var viaQuaternion = new BodySegment();
        viaQuaternion.UpdateOrientation(q);

        AssertQuaternion(viaQuaternion.Orientation, viaComponents.Orientation);
    }

    // ── Forward kinematics: parented ────────────────────────────────────────

    [TestMethod]
    public void UpdateOrientation_OnChild_OffsetsFromParentTip()
    {
        var root = new BodySegment { LinkLength = new Vector3F(1, 0, 0) };
        root.SetDh(0, 0, 0);
        root.UpdateOrientation(QuaternionF.Identity);   // tip at (1, 0, 0)

        var child = new BodySegment { LinkLength = new Vector3F(1, 0, 0), Parent = root };
        child.SetDh(0, 0, 0);
        child.UpdateOrientation(QuaternionF.Identity);  // its own link along +X

        AssertVector(new Vector3F(2, 0, 0), child.Position);
    }

    [TestMethod]
    public void UpdateOrientation_OnChild_ReportsOrientationRelativeToParent()
    {
        var root = new BodySegment();
        root.SetDh(0, 0, 0);
        root.UpdateOrientation(QuaternionF.FromEulerDegrees(0, 0, 30));

        var child = new BodySegment { Parent = root };
        child.SetDh(0, 0, 0);
        child.UpdateOrientation(QuaternionF.FromEulerDegrees(0, 0, 50));

        // 50° global against a 30° parent is 20° local.
        Assert.AreEqual(20f, QuaternionF.Yaw(child.LocalOrientation) * Rad2Deg, 1e-2f);
    }

    [TestMethod]
    public void UpdateOrientation_WithAlignedChain_LaysSegmentsEndToEnd()
    {
        var segments = new BodySegment[4];
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = new BodySegment { LinkLength = new Vector3F(0.5f, 0, 0) };
            segments[i].SetDh(0, 0, 0);
            if (i > 0) segments[i].Parent = segments[i - 1];
        }

        foreach (var s in segments) s.UpdateOrientation(QuaternionF.Identity);

        for (int i = 0; i < segments.Length; i++)
            AssertVector(new Vector3F(0.5f * (i + 1), 0, 0), segments[i].Position);
    }

    [TestMethod]
    public void UpdateOrientation_PropagatesToChild()
    {
        var root = new BodySegment { LinkLength = new Vector3F(1, 0, 0) };
        var child = new BodySegment { LinkLength = new Vector3F(1, 0, 0) };
        root.SetDh(0, 0, 0);
        child.SetDh(0, 0, 0);
        root.AddChild(child);
        child.Parent = root;

        // Only the root is driven; the child must be recomputed off the new parent tip.
        root.UpdateOrientation(QuaternionF.FromEulerDegrees(0, 0, 90));

        AssertVector(new Vector3F(0, 1, 0), root.Position);
        Assert.AreNotEqual(Vector3F.Zero, child.Position);
    }

    // ── Inverse kinematics helpers ──────────────────────────────────────────

    [TestMethod]
    public void InverseKinematicsArm_ReturnsLocalRollPitchYawInDegrees()
    {
        var root = new BodySegment();
        root.SetDh(0, 0, 0);
        root.UpdateOrientation(QuaternionF.Identity);

        var child = new BodySegment { Parent = root };
        child.SetDh(0, 0, 0);
        child.UpdateOrientation(QuaternionF.FromEulerDegrees(0, 0, 40));

        float[] angles = child.InverseKinematicsArm();

        Assert.HasCount(3, angles);
        Assert.AreEqual(0f, angles[0], 1e-2f, "roll");
        Assert.AreEqual(0f, angles[1], 1e-2f, "pitch");
        Assert.AreEqual(40f, angles[2], 1e-2f, "yaw");
    }

    [TestMethod]
    public void InverseKinematicsElbow_ReturnsTwoAngles()
    {
        var root = new BodySegment();
        root.SetDh(0, 0, 0);
        root.UpdateOrientation(QuaternionF.Identity);

        var child = new BodySegment { Parent = root };
        child.SetDh(0, 0, 0);
        child.UpdateOrientation(QuaternionF.FromEulerDegrees(0, 0, 25));

        float[] angles = child.InverseKinematicsElbow();

        Assert.HasCount(2, angles);
        Assert.IsFalse(float.IsNaN(angles[0]));
        Assert.IsFalse(float.IsNaN(angles[1]));
    }

    // ── Topology helpers ────────────────────────────────────────────────────

    [TestMethod]
    public void IsChildOf_IsTrueOnlyForTheImmediateParent()
    {
        var a = new BodySegment();
        var b = new BodySegment();
        var c = new BodySegment();
        a.AddChild(b);
        b.AddChild(c);

        Assert.IsTrue(b.IsChildOf(a));
        Assert.IsFalse(c.IsChildOf(a));
        Assert.IsFalse(a.IsChildOf(b));
    }

    [TestMethod]
    public void IsDescendantOf_WalksTheWholeChain()
    {
        var a = new BodySegment();
        var b = new BodySegment();
        var c = new BodySegment();
        var unrelated = new BodySegment();
        a.AddChild(b);
        b.AddChild(c);

        Assert.IsTrue(b.IsDescendantOf(a));
        Assert.IsTrue(c.IsDescendantOf(a));
        Assert.IsFalse(a.IsDescendantOf(c));
        Assert.IsFalse(unrelated.IsDescendantOf(a));
    }

    [TestMethod]
    public void IsDescendantOf_OnACyclicChain_Terminates()
    {
        // AddChild does not validate, so a caller can still build a cycle. The cycle
        // check itself must not be what hangs on it.
        var a = new BodySegment();
        var b = new BodySegment();
        a.AddChild(b);
        b.AddChild(a);

        var outsider = new BodySegment();

        Assert.IsTrue(b.IsDescendantOf(a));
        Assert.IsFalse(outsider.IsDescendantOf(a));
    }
}
