using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="Buffer"/>.
///
/// Unlike most blocks, <see cref="Buffer"/> does not publish an output stream — it accumulates
/// captured segments into its public <see cref="Buffer.dB"/> list as <c>(Target, Data)</c> pairs.
/// So these tests drive the block through its public <c>ReceiveInput</c> entry point (which runs
/// the protected <c>OnReceive</c> synchronously) and then assert on <see cref="Buffer.dB"/>.
///
/// Capture protocol reconstructed from the source:
/// <list type="bullet">
///   <item><description><see cref="Buffer.tempFlag"/> gates capture. While it is <c>false</c> the
///   block ignores incoming data (and finalizes any in-progress segment).</description></item>
///   <item><description>The first valid input received while collecting starts a segment: for a
///   vector the whole vector is the Target; for a matrix the first row is the Target and the
///   remaining rows become data.</description></item>
///   <item><description>Subsequent vectors are appended as data rows; subsequent matrices have
///   every row appended.</description></item>
///   <item><description>A <c>null</c> payload (or <see cref="Buffer.tempFlag"/> going <c>false</c>)
///   finalizes the segment: the collected rows become a dense matrix (one row per vector) and the
///   <c>(Target, Data)</c> pair is appended to <see cref="Buffer.dB"/>. With no data rows the Data
///   matrix is 0x0.</description></item>
/// </list>
/// </summary>
[TestClass]
public class BufferTests
{
    /// <summary>Drives the block via its public entry point; <c>null</c> ends a capture segment.</summary>
    private static void Feed(Buffer block, object? data) => block.ReceiveInput(sender: block, value: data!);

    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    [TestMethod]
    public void OnReceive_VectorCapture_StoresTargetThenDataRows()
    {
        // tempFlag on: first vector is the Target label, following vectors are data rows.
        // Target = [1,0]; data rows = [10,20,30] then [40,50,60]; null finalizes.
        // Expect one segment: Target [1,0], Data = [[10,20,30],[40,50,60]] (2x3).
        using var block = new Buffer(name: "Buf") { tempFlag = true };

        Feed(block, Vec(1.0, 0.0));            // start -> Target
        Feed(block, Vec(10.0, 20.0, 30.0));    // data row 0
        Feed(block, Vec(40.0, 50.0, 60.0));    // data row 1
        Feed(block, null);                     // stop -> finalize

        Assert.AreEqual(1, block.dB.Count);

        var (target, data) = block.dB[0];
        Assert.AreEqual(2, target.Count);
        Assert.AreEqual(1.0, target[0], 1e-9);
        Assert.AreEqual(0.0, target[1], 1e-9);

        Assert.AreEqual(2, data.RowCount);
        Assert.AreEqual(3, data.ColumnCount);
        Assert.AreEqual(10.0, data[0, 0], 1e-9);
        Assert.AreEqual(20.0, data[0, 1], 1e-9);
        Assert.AreEqual(30.0, data[0, 2], 1e-9);
        Assert.AreEqual(40.0, data[1, 0], 1e-9);
        Assert.AreEqual(50.0, data[1, 1], 1e-9);
        Assert.AreEqual(60.0, data[1, 2], 1e-9);
    }

    [TestMethod]
    public void OnReceive_MatrixStart_FirstRowIsTargetRemainingRowsAreData()
    {
        // Starting a segment with a matrix: row 0 is the Target, remaining rows are data.
        // Matrix [[1,2],[3,4],[5,6]] => Target [1,2], Data [[3,4],[5,6]] (2x2).
        using var block = new Buffer(name: "Buf") { tempFlag = true };
        var start = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0 },
            { 3.0, 4.0 },
            { 5.0, 6.0 },
        });

        Feed(block, start);   // start with matrix
        Feed(block, null);    // stop -> finalize

        Assert.AreEqual(1, block.dB.Count);

        var (target, data) = block.dB[0];
        Assert.AreEqual(2, target.Count);
        Assert.AreEqual(1.0, target[0], 1e-9);
        Assert.AreEqual(2.0, target[1], 1e-9);

        Assert.AreEqual(2, data.RowCount);
        Assert.AreEqual(2, data.ColumnCount);
        Assert.AreEqual(3.0, data[0, 0], 1e-9);
        Assert.AreEqual(4.0, data[0, 1], 1e-9);
        Assert.AreEqual(5.0, data[1, 0], 1e-9);
        Assert.AreEqual(6.0, data[1, 1], 1e-9);
    }

    [TestMethod]
    public void OnReceive_MatrixDuringCapture_AppendsEveryRowInOrder()
    {
        // Vector starts the segment (Target). A matrix received mid-capture appends ALL of its
        // rows as data; a trailing vector appends after them, preserving order.
        // Target [7,8,9]; matrix [[1,2,3],[4,5,6]]; vector [10,11,12].
        // Expect Data = [[1,2,3],[4,5,6],[10,11,12]] (3x3).
        using var block = new Buffer(name: "Buf") { tempFlag = true };
        var mid = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0, 3.0 },
            { 4.0, 5.0, 6.0 },
        });

        Feed(block, Vec(7.0, 8.0, 9.0));       // start -> Target
        Feed(block, mid);                      // append both rows
        Feed(block, Vec(10.0, 11.0, 12.0));    // append one more row
        Feed(block, null);                     // stop -> finalize

        Assert.AreEqual(1, block.dB.Count);

        var (target, data) = block.dB[0];
        Assert.AreEqual(3, target.Count);
        Assert.AreEqual(7.0, target[0], 1e-9);
        Assert.AreEqual(9.0, target[2], 1e-9);

        Assert.AreEqual(3, data.RowCount);
        Assert.AreEqual(3, data.ColumnCount);
        Assert.AreEqual(1.0, data[0, 0], 1e-9);
        Assert.AreEqual(3.0, data[0, 2], 1e-9);
        Assert.AreEqual(4.0, data[1, 0], 1e-9);
        Assert.AreEqual(6.0, data[1, 2], 1e-9);
        Assert.AreEqual(10.0, data[2, 0], 1e-9);
        Assert.AreEqual(12.0, data[2, 2], 1e-9);
    }

    [TestMethod]
    public void OnReceive_TempFlagClearedMidCapture_FinalizesAndDropsTriggeringPayload()
    {
        // tempFlag going false is a stop condition: the in-progress segment is finalized and the
        // payload that arrived under the cleared flag is NOT added. Target [1,1], one data row [2,2],
        // then flag off + [3,3] -> segment closes with only [[2,2]] (1x2); [3,3] is discarded.
        using var block = new Buffer(name: "Buf") { tempFlag = true };

        Feed(block, Vec(1.0, 1.0));   // start -> Target
        Feed(block, Vec(2.0, 2.0));   // data row 0
        block.tempFlag = false;       // gate closes
        Feed(block, Vec(3.0, 3.0));   // ignored payload -> triggers finalize only

        Assert.AreEqual(1, block.dB.Count);

        var (target, data) = block.dB[0];
        Assert.AreEqual(2, target.Count);
        Assert.AreEqual(1.0, target[0], 1e-9);

        Assert.AreEqual(1, data.RowCount);
        Assert.AreEqual(2, data.ColumnCount);
        Assert.AreEqual(2.0, data[0, 0], 1e-9);
        Assert.AreEqual(2.0, data[0, 1], 1e-9);
    }

    [TestMethod]
    public void OnReceive_TargetOnlyThenStop_ProducesEmptyDataMatrix()
    {
        // A segment that received only the Target (no data rows) finalizes with a 0x0 Data matrix.
        using var block = new Buffer(name: "Buf") { tempFlag = true };

        Feed(block, Vec(5.0, 6.0, 7.0));   // start -> Target only
        Feed(block, null);                 // stop -> finalize with no rows

        Assert.AreEqual(1, block.dB.Count);

        var (target, data) = block.dB[0];
        Assert.AreEqual(3, target.Count);
        Assert.AreEqual(5.0, target[0], 1e-9);
        Assert.AreEqual(7.0, target[2], 1e-9);

        Assert.AreEqual(0, data.RowCount);
        Assert.AreEqual(0, data.ColumnCount);
    }

    [TestMethod]
    public void OnReceive_TempFlagFalse_NoCaptureOccurs()
    {
        // With tempFlag never set, every payload is ignored and nothing is captured.
        using var block = new Buffer(name: "Buf"); // tempFlag defaults to false

        Feed(block, Vec(1.0, 2.0, 3.0));
        Feed(block, Vec(4.0, 5.0, 6.0));

        Assert.AreEqual(0, block.dB.Count);
    }

    [TestMethod]
    public void OnReceive_MultipleSegments_AppendedToDatabaseInOrder()
    {
        // A null between captures closes one segment while leaving tempFlag on, so the next vector
        // starts a fresh segment. Two segments accumulate in dB in arrival order.
        using var block = new Buffer(name: "Buf") { tempFlag = true };

        Feed(block, Vec(1.0));   // segment 0 Target
        Feed(block, Vec(2.0));   // segment 0 data
        Feed(block, Vec(3.0));   // segment 0 data
        Feed(block, null);       // close segment 0

        Feed(block, Vec(10.0));  // segment 1 Target
        Feed(block, Vec(20.0));  // segment 1 data
        Feed(block, null);       // close segment 1

        Assert.AreEqual(2, block.dB.Count);

        var (target0, data0) = block.dB[0];
        Assert.AreEqual(1, target0.Count);
        Assert.AreEqual(1.0, target0[0], 1e-9);
        Assert.AreEqual(2, data0.RowCount);
        Assert.AreEqual(1, data0.ColumnCount);
        Assert.AreEqual(2.0, data0[0, 0], 1e-9);
        Assert.AreEqual(3.0, data0[1, 0], 1e-9);

        var (target1, data1) = block.dB[1];
        Assert.AreEqual(1, target1.Count);
        Assert.AreEqual(10.0, target1[0], 1e-9);
        Assert.AreEqual(1, data1.RowCount);
        Assert.AreEqual(1, data1.ColumnCount);
        Assert.AreEqual(20.0, data1[0, 0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_UnsupportedPayload_IsIgnoredAndCaptureContinues()
    {
        // A payload that is neither Vector nor Matrix (and not null) is skipped: it adds no data
        // row and does not end the segment. Target [1,2]; a string is ignored; then data [3,4].
        // Expect Data = [[3,4]] (1x2) — the string contributed nothing.
        using var block = new Buffer(name: "Buf") { tempFlag = true };

        Feed(block, Vec(1.0, 2.0));   // start -> Target
        Feed(block, "not a vector");  // unsupported -> ignored, capture continues
        Feed(block, Vec(3.0, 4.0));   // data row 0
        Feed(block, null);            // stop -> finalize

        Assert.AreEqual(1, block.dB.Count);

        var (target, data) = block.dB[0];
        Assert.AreEqual(2, target.Count);
        Assert.AreEqual(1.0, target[0], 1e-9);
        Assert.AreEqual(2.0, target[1], 1e-9);

        Assert.AreEqual(1, data.RowCount);
        Assert.AreEqual(2, data.ColumnCount);
        Assert.AreEqual(3.0, data[0, 0], 1e-9);
        Assert.AreEqual(4.0, data[0, 1], 1e-9);
    }
}
