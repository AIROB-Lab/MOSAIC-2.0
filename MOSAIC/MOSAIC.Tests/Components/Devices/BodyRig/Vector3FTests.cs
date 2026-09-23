using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.BodyRig;

namespace MOSAIC.Tests.Components.Devices.BodyRig;

/// <summary>
/// Known-answer tests for <see cref="Vector3F"/>, the immutable 3-vector used for
/// body-segment positions and link lengths. Pure value type, no block wiring involved.
/// </summary>
[TestClass]
public class Vector3FTests
{
    private const float Tol = 1e-6f;

    [TestMethod]
    public void Constructor_StoresComponents()
    {
        var v = new Vector3F(1.5f, -2.5f, 3f);

        Assert.AreEqual(1.5f, v.X);
        Assert.AreEqual(-2.5f, v.Y);
        Assert.AreEqual(3f, v.Z);
    }

    [TestMethod]
    public void Zero_IsAllZeroes()
    {
        Assert.AreEqual(new Vector3F(0, 0, 0), Vector3F.Zero);
    }

    [TestMethod]
    public void Indexer_ReturnsComponentsInOrder()
    {
        var v = new Vector3F(7, 8, 9);

        Assert.AreEqual(7f, v[0]);
        Assert.AreEqual(8f, v[1]);
        Assert.AreEqual(9f, v[2]);
    }

    [TestMethod]
    public void Indexer_OutOfRange_Throws()
    {
        var v = new Vector3F(1, 2, 3);

        Assert.ThrowsExactly<IndexOutOfRangeException>(() => _ = v[3]);
        Assert.ThrowsExactly<IndexOutOfRangeException>(() => _ = v[-1]);
    }

    [TestMethod]
    public void Addition_And_Subtraction_AreComponentWise()
    {
        var a = new Vector3F(1, 2, 3);
        var b = new Vector3F(10, 20, 30);

        Assert.AreEqual(new Vector3F(11, 22, 33), a + b);
        Assert.AreEqual(new Vector3F(9, 18, 27), b - a);
    }

    [TestMethod]
    public void Negation_FlipsEveryComponent()
    {
        Assert.AreEqual(new Vector3F(-1, 2, -3), -new Vector3F(1, -2, 3));
    }

    [TestMethod]
    public void ScalarMultiplication_IsCommutative()
    {
        var v = new Vector3F(1, -2, 3);

        Assert.AreEqual(new Vector3F(2, -4, 6), v * 2f);
        Assert.AreEqual(new Vector3F(2, -4, 6), 2f * v);
    }

    [TestMethod]
    public void ScalarDivision_ScalesDown()
    {
        Assert.AreEqual(new Vector3F(1, -2, 3), new Vector3F(2, -4, 6) / 2f);
    }

    [TestMethod]
    public void Magnitude_IsEuclideanLength()
    {
        Assert.AreEqual(5f, new Vector3F(3, 4, 0).Magnitude(), Tol);
        Assert.AreEqual(0f, Vector3F.Zero.Magnitude(), Tol);
    }

    [TestMethod]
    public void Normalized_HasUnitLength_AndKeepsDirection()
    {
        var n = new Vector3F(0, 3, 4).Normalized();

        Assert.AreEqual(1f, n.Magnitude(), Tol);
        Assert.AreEqual(0f, n.X, Tol);
        Assert.AreEqual(0.6f, n.Y, Tol);
        Assert.AreEqual(0.8f, n.Z, Tol);
    }

    [TestMethod]
    public void Dot_MatchesKnownValues()
    {
        Assert.AreEqual(32f, Vector3F.Dot(new Vector3F(1, 2, 3), new Vector3F(4, 5, 6)), Tol);
        Assert.AreEqual(0f, Vector3F.Dot(new Vector3F(1, 0, 0), new Vector3F(0, 1, 0)), Tol);
    }

    [TestMethod]
    public void Cross_FollowsRightHandRule()
    {
        var x = new Vector3F(1, 0, 0);
        var y = new Vector3F(0, 1, 0);

        Assert.AreEqual(new Vector3F(0, 0, 1), Vector3F.Cross(x, y));
        Assert.AreEqual(new Vector3F(0, 0, -1), Vector3F.Cross(y, x));
    }

    [TestMethod]
    public void Cross_OfParallelVectors_IsZero()
    {
        var a = new Vector3F(2, 4, 6);

        Assert.AreEqual(Vector3F.Zero, Vector3F.Cross(a, a * 3f));
    }

    [TestMethod]
    public void Equality_ComparesAllComponents()
    {
        var a = new Vector3F(1, 2, 3);

        Assert.IsTrue(a == new Vector3F(1, 2, 3));
        Assert.IsFalse(a != new Vector3F(1, 2, 3));
        Assert.IsTrue(a != new Vector3F(1, 2, 4));
        Assert.AreEqual(a.GetHashCode(), new Vector3F(1, 2, 3).GetHashCode());
    }

    [TestMethod]
    public void Equals_AgainstOtherType_IsFalse()
    {
        Assert.IsFalse(new Vector3F(1, 2, 3).Equals("not a vector"));
    }
}
