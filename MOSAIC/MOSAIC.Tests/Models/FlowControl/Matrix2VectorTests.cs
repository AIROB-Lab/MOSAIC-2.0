using System;
using System.Threading;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="Matrix2Vector"/>.
///
/// The block streams a buffered matrix frame row-by-row: rows = timesteps, columns = channels.
/// It has a dual-input contract keyed on the SENDER NAME:
///   * A frame (Matrix or Vector) arriving from any sender whose name is NOT the timer source
///     is buffered and the row cursor is reset to 0 — nothing is published.
///   * A tick arriving from a sender whose name EQUALS the configured timer source publishes the
///     next row as a <c>Vector[nChannels]</c> (row 0 on the first tick, row 1 on the second, ...).
///   * Once every row has been emitted (cursor >= RowCount) further ticks are silent until a new
///     frame arrives and resets the cursor.
///
/// A Vector frame is treated as a single-column matrix (N timesteps x 1 channel), so each tick
/// emits a length-1 vector holding successive elements of that vector.
///
/// Because the shared <see cref="MOSAIC.Tests.TestSupport.BlockHarness"/> always sends with
/// <c>sender: block</c> (whose name never matches the timer source) and issues only ONE
/// <c>ReceiveInput</c>, it cannot express the required store-then-tick sequence. These tests drive
/// the block directly through its public <see cref="BaseBlock.ReceiveInput"/> surface with a
/// string sender name and capture published rows with a small test-only subscriber.
/// </summary>
[TestClass]
public class Matrix2VectorTests
{
    private const string TimerName = "clock";
    private const string DataName = "window";

    /// <summary>Test-only subscriber that records every published value in order.</summary>
    private sealed class RowCollector : ISubscriber
    {
        private readonly object _gate = new();
        private readonly System.Collections.Generic.List<object> _values = new();
        private readonly AutoResetEvent _received = new(false);

        public void ReceiveInput(object sender, object value)
        {
            lock (_gate) _values.Add(value);
            _received.Set();
        }

        /// <summary>Waits until at least one new value has arrived; returns false on timeout.</summary>
        public bool WaitForOne(int timeoutMs = 2000) => _received.WaitOne(timeoutMs);

        public int Count { get { lock (_gate) return _values.Count; } }

        public Vector<double> VectorAt(int index)
        {
            lock (_gate) return (Vector<double>)_values[index];
        }
    }

    /// <summary>Buffers a frame (from a non-timer sender) so no row is published yet.</summary>
    private static void StoreFrame(Matrix2Vector block, object frame)
        => block.ReceiveInput(sender: DataName, value: frame);

    /// <summary>Issues one timer tick (from the timer-named sender) to emit the next row.</summary>
    private static void Tick(Matrix2Vector block)
        => block.ReceiveInput(sender: TimerName, value: 0.0);

    [TestMethod]
    public void OnReceive_TicksAfterFrame_StreamsRowsInRowMajorOrder()
    {
        // Frame is 2 rows x 3 cols. Row 0 = [1,2,3], Row 1 = [4,5,6].
        // Tick 1 -> Row(0) = [1,2,3]; Tick 2 -> Row(1) = [4,5,6]; each vector has Count = nChannels = 3.
        using var block = new Matrix2Vector(name: "m2v", desiredRate: 0, timerSource: TimerName);
        var collector = new RowCollector();
        block.AddSubscriber(collector);
        var frame = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0, 3.0 },
            { 4.0, 5.0, 6.0 },
        });

        StoreFrame(block, frame);
        Tick(block);
        Assert.IsTrue(collector.WaitForOne(), "First tick did not publish a row.");
        Tick(block);
        Assert.IsTrue(collector.WaitForOne(), "Second tick did not publish a row.");

        Assert.AreEqual(2, collector.Count);

        var row0 = collector.VectorAt(0);
        Assert.AreEqual(3, row0.Count);          // nChannels = column count
        Assert.AreEqual(1.0, row0[0], 1e-9);     // frame[0,0]
        Assert.AreEqual(2.0, row0[1], 1e-9);     // frame[0,1]
        Assert.AreEqual(3.0, row0[2], 1e-9);     // frame[0,2]

        var row1 = collector.VectorAt(1);
        Assert.AreEqual(3, row1.Count);
        Assert.AreEqual(4.0, row1[0], 1e-9);     // frame[1,0]
        Assert.AreEqual(5.0, row1[1], 1e-9);     // frame[1,1]
        Assert.AreEqual(6.0, row1[2], 1e-9);     // frame[1,2]
    }

    [TestMethod]
    public void OnReceive_SingleChannelFrame_EmitsLengthOneVectorsPerRow()
    {
        // 3 rows x 1 col => each row is a single-element vector: [10], [20], [30].
        using var block = new Matrix2Vector(name: "m2v", desiredRate: 0, timerSource: TimerName);
        var collector = new RowCollector();
        block.AddSubscriber(collector);
        var frame = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 10.0 },
            { 20.0 },
            { 30.0 },
        });

        StoreFrame(block, frame);
        for (int i = 0; i < 3; i++)
        {
            Tick(block);
            Assert.IsTrue(collector.WaitForOne(), $"Tick {i} did not publish a row.");
        }

        Assert.AreEqual(3, collector.Count);

        Assert.AreEqual(1, collector.VectorAt(0).Count);
        Assert.AreEqual(10.0, collector.VectorAt(0)[0], 1e-9); // row 0
        Assert.AreEqual(20.0, collector.VectorAt(1)[0], 1e-9); // row 1
        Assert.AreEqual(30.0, collector.VectorAt(2)[0], 1e-9); // row 2
    }

    [TestMethod]
    public void OnReceive_VectorFrame_TreatedAsSingleColumnMatrix()
    {
        // A Vector [7,8] is buffered as a 2x1 matrix (2 timesteps x 1 channel),
        // so ticks emit [7] then [8], each a length-1 vector.
        using var block = new Matrix2Vector(name: "m2v", desiredRate: 0, timerSource: TimerName);
        var collector = new RowCollector();
        block.AddSubscriber(collector);
        var frame = Vector<double>.Build.Dense(new[] { 7.0, 8.0 });

        StoreFrame(block, frame);
        Tick(block);
        Assert.IsTrue(collector.WaitForOne(), "First tick did not publish a row.");
        Tick(block);
        Assert.IsTrue(collector.WaitForOne(), "Second tick did not publish a row.");

        Assert.AreEqual(2, collector.Count);
        Assert.AreEqual(1, collector.VectorAt(0).Count);
        Assert.AreEqual(7.0, collector.VectorAt(0)[0], 1e-9); // element 0
        Assert.AreEqual(8.0, collector.VectorAt(1)[0], 1e-9); // element 1
    }

    [TestMethod]
    public void OnReceive_TickWithNoBufferedFrame_PublishesNothing()
    {
        // No frame has been stored, so a tick must be silent (cursor guard: _currentFrame is null).
        using var block = new Matrix2Vector(name: "m2v", desiredRate: 0, timerSource: TimerName);
        var collector = new RowCollector();
        block.AddSubscriber(collector);

        Tick(block);

        Assert.IsFalse(collector.WaitForOne(300), "Block published a row without any buffered frame.");
        Assert.AreEqual(0, collector.Count);
    }

    [TestMethod]
    public void OnReceive_MoreTicksThanRows_StopsPublishingAfterFrameExhausted()
    {
        // Frame has exactly 1 row. Tick 1 emits it; the cursor now equals RowCount, so tick 2 is silent.
        using var block = new Matrix2Vector(name: "m2v", desiredRate: 0, timerSource: TimerName);
        var collector = new RowCollector();
        block.AddSubscriber(collector);
        var frame = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 5.0, 6.0 },
        });

        StoreFrame(block, frame);
        Tick(block);
        Assert.IsTrue(collector.WaitForOne(), "First tick did not publish the only row.");
        Tick(block); // frame exhausted -> should stay silent

        Assert.IsFalse(collector.WaitForOne(300), "Block published beyond the end of the frame.");
        Assert.AreEqual(1, collector.Count);
        Assert.AreEqual(2, collector.VectorAt(0).Count);
        Assert.AreEqual(5.0, collector.VectorAt(0)[0], 1e-9); // frame[0,0]
        Assert.AreEqual(6.0, collector.VectorAt(0)[1], 1e-9); // frame[0,1]
    }

    [TestMethod]
    public void OnReceive_NewFrameArrives_ResetsCursorToFirstRow()
    {
        // Store frame A, consume its row 0, then store frame B: the cursor resets so the next
        // tick emits row 0 of B, not a later row.
        using var block = new Matrix2Vector(name: "m2v", desiredRate: 0, timerSource: TimerName);
        var collector = new RowCollector();
        block.AddSubscriber(collector);
        var frameA = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 1.0 },
            { 2.0, 2.0 },
        });
        var frameB = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 9.0, 8.0 },
            { 7.0, 6.0 },
        });

        StoreFrame(block, frameA);
        Tick(block); // emits frameA row 0 = [1,1]
        Assert.IsTrue(collector.WaitForOne(), "First tick did not publish frame A row 0.");
        StoreFrame(block, frameB); // resets cursor to 0
        Tick(block); // emits frameB row 0 = [9,8], NOT frameA row 1
        Assert.IsTrue(collector.WaitForOne(), "Tick after new frame did not publish.");

        Assert.AreEqual(2, collector.Count);
        Assert.AreEqual(1.0, collector.VectorAt(0)[0], 1e-9); // frameA[0,0]
        Assert.AreEqual(1.0, collector.VectorAt(0)[1], 1e-9); // frameA[0,1]
        Assert.AreEqual(9.0, collector.VectorAt(1)[0], 1e-9); // frameB[0,0] (cursor was reset)
        Assert.AreEqual(8.0, collector.VectorAt(1)[1], 1e-9); // frameB[0,1]
    }

    [TestMethod]
    public void Constructor_EmptyTimerSource_ThrowsArgumentException()
    {
        // The constructor requires a non-empty timer source name.
        Assert.ThrowsExactly<ArgumentException>(
            () => new Matrix2Vector(name: "m2v", desiredRate: 0, timerSource: ""));
    }

    [TestMethod]
    public void Constructor_WhitespaceTimerSource_ThrowsArgumentException()
    {
        // Whitespace is treated as empty by the IsNullOrWhiteSpace guard.
        Assert.ThrowsExactly<ArgumentException>(
            () => new Matrix2Vector(name: "m2v", desiredRate: 0, timerSource: "   "));
    }
}
