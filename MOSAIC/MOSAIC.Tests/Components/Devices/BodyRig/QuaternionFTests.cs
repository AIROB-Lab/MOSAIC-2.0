using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.BodyRig;

namespace MOSAIC.Tests.Components.Devices.BodyRig;

/// <summary>
/// Known-answer tests for <see cref="QuaternionF"/> — the Hamilton product, vector rotation,
/// and the Euler conversions the BodyRig calibration UI depends on.
/// </summary>
/// <remarks>
/// The Euler pair is the load-bearing part: <see cref="QuaternionF.FromEulerDegrees(float,float,float)"/>
/// and <see cref="QuaternionF.ToEulerAngles"/> must be exact inverses in the intrinsic Z-Y-X
/// convention, because the segment inspector reads DH angles out of a segment with one and writes
/// them straight back with the other. They previously used mismatched composition orders, so a
/// read-then-write of an untouched segment silently rotated it.
/// </remarks>
[TestClass]
public class QuaternionFTests
{
    private const float Tol = 1e-5f;
    private const float Rad2Deg = 180f / MathF.PI;

    private static void AssertQuaternion(QuaternionF expected, QuaternionF actual, float tol = Tol)
    {
        Assert.AreEqual(expected.X, actual.X, tol, "X");
        Assert.AreEqual(expected.Y, actual.Y, tol, "Y");
        Assert.AreEqual(expected.Z, actual.Z, tol, "Z");
        Assert.AreEqual(expected.W, actual.W, tol, "W");
    }

    private static void AssertVector(Vector3F expected, Vector3F actual, float tol = Tol)
    {
        Assert.AreEqual(expected.X, actual.X, tol, "X");
        Assert.AreEqual(expected.Y, actual.Y, tol, "Y");
        Assert.AreEqual(expected.Z, actual.Z, tol, "Z");
    }

    // ── Basics ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Identity_IsUnitScalar()
    {
        Assert.AreEqual(new QuaternionF(0, 0, 0, 1), QuaternionF.Identity);
        Assert.AreEqual(1f, QuaternionF.Identity.Magnitude(), Tol);
    }

    [TestMethod]
    public void Constructor_TakesXyzwOrder()
    {
        var q = new QuaternionF(1, 2, 3, 4);

        Assert.AreEqual(1f, q.X);
        Assert.AreEqual(2f, q.Y);
        Assert.AreEqual(3f, q.Z);
        Assert.AreEqual(4f, q.W);
    }

    [TestMethod]
    public void Magnitude_IsL2Norm()
    {
        Assert.AreEqual(MathF.Sqrt(30f), new QuaternionF(1, 2, 3, 4).Magnitude(), Tol);
    }

    [TestMethod]
    public void Conjugate_NegatesVectorPartOnly()
    {
        AssertQuaternion(new QuaternionF(-1, -2, -3, 4), new QuaternionF(1, 2, 3, 4).Conjugate());
    }

    [TestMethod]
    public void Normalized_ProducesUnitLength()
    {
        var n = new QuaternionF(1, 2, 3, 4).Normalized();

        Assert.AreEqual(1f, n.Magnitude(), Tol);
    }

    [TestMethod]
    public void Normalized_OfZeroQuaternion_FallsBackToIdentity()
    {
        // Sensor dropouts hand us all-zero quaternions; dividing by a zero magnitude
        // would spray NaN through the whole kinematic chain.
        var n = new QuaternionF(0, 0, 0, 0).Normalized();

        Assert.AreEqual(QuaternionF.Identity, n);
    }

    [TestMethod]
    public void ArithmeticOperators_AreComponentWise()
    {
        var a = new QuaternionF(1, 2, 3, 4);
        var b = new QuaternionF(10, 20, 30, 40);

        AssertQuaternion(new QuaternionF(11, 22, 33, 44), a + b);
        AssertQuaternion(new QuaternionF(9, 18, 27, 36), b - a);
        AssertQuaternion(new QuaternionF(-1, -2, -3, -4), -a);
        AssertQuaternion(new QuaternionF(2, 4, 6, 8), a * 2f);
        AssertQuaternion(new QuaternionF(2, 4, 6, 8), 2f * a);
        AssertQuaternion(new QuaternionF(5, 10, 15, 20), b / 2f);
    }

    // ── Hamilton product ────────────────────────────────────────────────────

    [TestMethod]
    public void Product_WithIdentity_IsUnchanged()
    {
        var q = new QuaternionF(0.2f, -0.4f, 0.5f, 0.74f).Normalized();

        AssertQuaternion(q, q * QuaternionF.Identity);
        AssertQuaternion(q, QuaternionF.Identity * q);
    }

    [TestMethod]
    public void Product_FollowsHamiltonRules()
    {
        var i = new QuaternionF(1, 0, 0, 0);
        var j = new QuaternionF(0, 1, 0, 0);
        var k = new QuaternionF(0, 0, 1, 0);

        AssertQuaternion(k, i * j);            // ij = k
        AssertQuaternion(-k, j * i);           // ji = -k
        AssertQuaternion(i, j * k);            // jk = i
        AssertQuaternion(new QuaternionF(0, 0, 0, -1), i * i); // i² = -1
    }

    [TestMethod]
    public void Product_WithConjugate_YieldsSquaredMagnitude()
    {
        var q = new QuaternionF(0.3f, 0.4f, 0.5f, 0.6f);

        AssertQuaternion(new QuaternionF(0, 0, 0, q.Magnitude() * q.Magnitude()), q * q.Conjugate());
    }

    // ── Vector rotation ─────────────────────────────────────────────────────

    [TestMethod]
    public void RotatingVector_ByIdentity_LeavesItUnchanged()
    {
        var v = new Vector3F(1, 2, 3);

        AssertVector(v, QuaternionF.Identity * v);
    }

    [TestMethod]
    public void RotatingXAxis_By90DegreesYaw_GivesYAxis()
    {
        var q = QuaternionF.FromEulerDegrees(0, 0, 90);

        AssertVector(new Vector3F(0, 1, 0), q * new Vector3F(1, 0, 0));
    }

    [TestMethod]
    public void RotatingYAxis_By90DegreesRoll_GivesZAxis()
    {
        var q = QuaternionF.FromEulerDegrees(90, 0, 0);

        AssertVector(new Vector3F(0, 0, 1), q * new Vector3F(0, 1, 0));
    }

    [TestMethod]
    public void RotatingZAxis_By90DegreesPitch_GivesXAxis()
    {
        var q = QuaternionF.FromEulerDegrees(0, 90, 0);

        AssertVector(new Vector3F(1, 0, 0), q * new Vector3F(0, 0, 1));
    }

    [TestMethod]
    public void Rotation_PreservesVectorLength()
    {
        var q = QuaternionF.FromEulerDegrees(23, -41, 67);
        var v = new Vector3F(1, -2, 3);

        Assert.AreEqual(v.Magnitude(), (q * v).Magnitude(), Tol);
    }

    // ── Euler: single axis ──────────────────────────────────────────────────

    [TestMethod]
    public void FromEulerDegrees_RollArgument_RotatesAboutX()
    {
        var q = QuaternionF.FromEulerDegrees(60, 0, 0);

        AssertQuaternion(new QuaternionF(MathF.Sin(MathF.PI / 6f), 0, 0, MathF.Cos(MathF.PI / 6f)), q);
    }

    [TestMethod]
    public void FromEulerDegrees_PitchArgument_RotatesAboutY()
    {
        var q = QuaternionF.FromEulerDegrees(0, 60, 0);

        AssertQuaternion(new QuaternionF(0, MathF.Sin(MathF.PI / 6f), 0, MathF.Cos(MathF.PI / 6f)), q);
    }

    [TestMethod]
    public void FromEulerDegrees_YawArgument_RotatesAboutZ()
    {
        var q = QuaternionF.FromEulerDegrees(0, 0, 60);

        AssertQuaternion(new QuaternionF(0, 0, MathF.Sin(MathF.PI / 6f), MathF.Cos(MathF.PI / 6f)), q);
    }

    [TestMethod]
    public void FromEulerDegrees_AllZero_IsIdentity()
    {
        AssertQuaternion(QuaternionF.Identity, QuaternionF.FromEulerDegrees(0, 0, 0));
    }

    [TestMethod]
    public void FromEulerDegrees_ProducesUnitQuaternion()
    {
        Assert.AreEqual(1f, QuaternionF.FromEulerDegrees(31, -47, 122).Magnitude(), Tol);
    }

    // ── Euler: extraction ───────────────────────────────────────────────────

    [TestMethod]
    public void RollPitchYaw_ExtractSingleAxisRotations()
    {
        Assert.AreEqual(35f, QuaternionF.Roll(QuaternionF.FromEulerDegrees(35, 0, 0)) * Rad2Deg, 1e-3f);
        Assert.AreEqual(35f, QuaternionF.Pitch(QuaternionF.FromEulerDegrees(0, 35, 0)) * Rad2Deg, 1e-3f);
        Assert.AreEqual(35f, QuaternionF.Yaw(QuaternionF.FromEulerDegrees(0, 0, 35)) * Rad2Deg, 1e-3f);
    }

    [TestMethod]
    public void Pitch_AtGimbalLock_StaysFiniteAndNear90()
    {
        // asin()'s derivative blows up at ±1, so a straight-up quaternion only recovers
        // 90° to a few hundredths of a degree in single precision. What matters is that
        // it is finite and unmistakably vertical.
        var straightUp = QuaternionF.FromEulerDegrees(0, 90, 0);

        float pitch = QuaternionF.Pitch(straightUp) * Rad2Deg;

        Assert.IsTrue(float.IsFinite(pitch), $"Pitch was {pitch} at gimbal lock.");
        Assert.AreEqual(90f, pitch, 0.05f);
    }

    [TestMethod]
    public void Pitch_WhenTheSineArgumentExceedsOne_ClampsToPlusOrMinus90()
    {
        // This is the branch the guard exists for: an unnormalised quaternion pushes
        // 2(wy − zx) past ±1, where a bare asin() would return NaN.
        float up = QuaternionF.Pitch(new QuaternionF(0, 1, 0, 1)) * Rad2Deg;   // sin arg = +2
        float down = QuaternionF.Pitch(new QuaternionF(0, -1, 0, 1)) * Rad2Deg; // sin arg = −2

        Assert.AreEqual(90f, up, 1e-3f);
        Assert.AreEqual(-90f, down, 1e-3f);
    }

    [TestMethod]
    public void ToEulerAngles_ReturnsRollPitchYawInThatOrder()
    {
        var e = QuaternionF.FromEulerDegrees(10, 20, 30).ToEulerAngles();

        Assert.AreEqual(10f, e.X * Rad2Deg, 1e-3f, "X should be roll");
        Assert.AreEqual(20f, e.Y * Rad2Deg, 1e-3f, "Y should be pitch");
        Assert.AreEqual(30f, e.Z * Rad2Deg, 1e-3f, "Z should be yaw");
    }

    // ── Euler: round trip (the regression) ──────────────────────────────────

    [TestMethod]
    [DataRow(0f, 0f, 0f)]
    [DataRow(30f, 0f, 0f)]
    [DataRow(0f, 30f, 0f)]
    [DataRow(0f, 0f, -90f)]
    [DataRow(10f, 20f, 30f)]
    [DataRow(45f, 15f, -60f)]
    [DataRow(-20f, 40f, 80f)]
    [DataRow(179f, -89f, 179f)]
    public void FromEulerDegrees_And_ToEulerAngles_AreInverses(float roll, float pitch, float yaw)
    {
        var e = QuaternionF.FromEulerDegrees(roll, pitch, yaw).ToEulerAngles();

        Assert.AreEqual(roll, e.X * Rad2Deg, 1e-2f, "roll");
        Assert.AreEqual(pitch, e.Y * Rad2Deg, 1e-2f, "pitch");
        Assert.AreEqual(yaw, e.Z * Rad2Deg, 1e-2f, "yaw");
    }

    [TestMethod]
    public void FromEulerDegrees_VectorOverload_MatchesComponentOverload()
    {
        var expected = QuaternionF.FromEulerDegrees(12, -34, 56);
        var actual = QuaternionF.FromEulerDegrees(new Vector3F(12, -34, 56));

        AssertQuaternion(expected, actual);
    }

    [TestMethod]
    public void EulerRoundTrip_ThroughVectorOverload_PreservesRotation()
    {
        // This is exactly what the segment inspector does: read angles out of a
        // segment, then write the same angles back. It must be a no-op.
        var original = QuaternionF.FromEulerDegrees(25, -35, 115);
        var reconstructed = QuaternionF.FromEulerDegrees(original.ToEulerAngles() * Rad2Deg);

        AssertQuaternion(original, reconstructed, 1e-4f);
    }

    [TestMethod]
    public void FromEulerDegrees_ComposesAsZThenYThenX()
    {
        // Intrinsic Z-Y-X: q = q_z(yaw) · q_y(pitch) · q_x(roll)
        var qx = QuaternionF.FromEulerDegrees(10, 0, 0);
        var qy = QuaternionF.FromEulerDegrees(0, 20, 0);
        var qz = QuaternionF.FromEulerDegrees(0, 0, 30);

        AssertQuaternion(qz * qy * qx, QuaternionF.FromEulerDegrees(10, 20, 30));
    }

    // ── Equality & formatting ───────────────────────────────────────────────

    [TestMethod]
    public void Equality_ComparesAllFourComponents()
    {
        var a = new QuaternionF(1, 2, 3, 4);

        Assert.IsTrue(a == new QuaternionF(1, 2, 3, 4));
        Assert.IsTrue(a != new QuaternionF(1, 2, 3, 5));
        Assert.AreEqual(a.GetHashCode(), new QuaternionF(1, 2, 3, 4).GetHashCode());
        Assert.IsFalse(a.Equals("not a quaternion"));
    }

    [TestMethod]
    public void ToString_ListsComponentsInXyzwOrder()
    {
        Assert.AreEqual("1,2,3,4", new QuaternionF(1, 2, 3, 4).ToString());
    }
}
