using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;

namespace MOSAIC.Tests.Components.Basics;

/// <summary>
/// Known-answer tests for <see cref="CircularBuffer{T}"/>: a fixed-size ring buffer that
/// overwrites the oldest element once full.
/// </summary>
[TestClass]
public class CircularBufferTests
{
    [TestMethod]
    public void Add_WithinCapacity_TracksCountFirstAndLast()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        Assert.AreEqual(3, buffer.Count);
        Assert.IsTrue(buffer.IsFull);
        Assert.AreEqual(1, buffer.First);
        Assert.AreEqual(3, buffer.Last);
    }

    [TestMethod]
    public void Add_BeyondCapacity_OverwritesOldest()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        buffer.Add(4); // overwrites 1
        Assert.AreEqual(3, buffer.Count);
        Assert.AreEqual(2, buffer.First);
        Assert.AreEqual(4, buffer.Last);

        buffer.Add(5); // overwrites 2
        Assert.AreEqual(3, buffer.First);
        Assert.AreEqual(5, buffer.Last);
    }

    [TestMethod]
    public void Count_GrowsWhileFillingThenStaysAtCapacity()
    {
        var buffer = new CircularBuffer<int>(2);
        Assert.AreEqual(0, buffer.Count);

        buffer.Add(10);
        Assert.AreEqual(1, buffer.Count);

        buffer.Add(20);
        Assert.AreEqual(2, buffer.Count);

        buffer.Add(30); // wrap
        Assert.AreEqual(2, buffer.Count);
    }

    [TestMethod]
    public void First_OnEmptyBuffer_Throws()
    {
        var buffer = new CircularBuffer<int>(3);
        Assert.ThrowsExactly<InvalidOperationException>(() => { _ = buffer.First; });
    }

    [TestMethod]
    public void Last_OnEmptyBuffer_Throws()
    {
        var buffer = new CircularBuffer<int>(3);
        Assert.ThrowsExactly<InvalidOperationException>(() => { _ = buffer.Last; });
    }

    [TestMethod]
    public void Constructor_NonPositiveCapacity_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CircularBuffer<int>(0));
    }

    [TestMethod]
    public void Reset_ClearsCountAndFullState()
    {
        var buffer = new CircularBuffer<int>(2);
        buffer.Add(1);
        buffer.Add(2); // full

        buffer.Reset();

        Assert.AreEqual(0, buffer.Count);
        Assert.IsFalse(buffer.IsFull);
        Assert.ThrowsExactly<InvalidOperationException>(() => { _ = buffer.First; });
    }
}
