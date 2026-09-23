using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.FlowControl;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="Projector"/>: selects channels by index.
/// Vector input => output[i] = input[indices[i]] over the valid indices.
/// Matrix input [rows × channels] => output column i = input column indices[i]; all rows preserved.
/// Indices outside [0, N−1] are silently skipped; if no index is valid the block publishes nothing.
/// </summary>
[TestClass]
public class ProjectorTests
{
    [TestMethod]
    public void OnReceive_VectorSubsetSelection_PublishesSelectedElementsInIndexOrder()
    {
        // input = [10, 20, 30, 40]; indices = [0, 2] => output = [input[0], input[2]] = [10, 30].
        using var block = new Projector(name: "P", indices: new[] { 0, 2 });
        var input = Vector<double>.Build.Dense(new[] { 10.0, 20.0, 30.0, 40.0 });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(10.0, result[0], 1e-9); // input[0]
        Assert.AreEqual(30.0, result[1], 1e-9); // input[2]
    }

    [TestMethod]
    public void OnReceive_VectorSingleIndex_PublishesOneElement()
    {
        // input = [1, 2, 3, 4, 5]; indices = [3] => output = [input[3]] = [4].
        using var block = new Projector(name: "P", indices: new[] { 3 });
        var input = Vector<double>.Build.Dense(new[] { 1.0, 2.0, 3.0, 4.0, 5.0 });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(4.0, result[0], 1e-9); // input[3]
    }

    [TestMethod]
    public void OnReceive_VectorReorderAndDuplicateIndices_TakesEachIndexIndependently()
    {
        // input = [10, 20, 30]; indices = [1, 1, 0]
        // => output = [input[1], input[1], input[0]] = [20, 20, 10]. Each index resolved independently.
        using var block = new Projector(name: "P", indices: new[] { 1, 1, 0 });
        var input = Vector<double>.Build.Dense(new[] { 10.0, 20.0, 30.0 });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(20.0, result[0], 1e-9); // input[1]
        Assert.AreEqual(20.0, result[1], 1e-9); // input[1]
        Assert.AreEqual(10.0, result[2], 1e-9); // input[0]
    }

    [TestMethod]
    public void OnReceive_VectorWithSomeInvalidIndices_SkipsInvalidAndKeepsValid()
    {
        // input = [5, 6, 7] (count = 3, valid range 0..2); indices = [1, 99].
        // Index 99 is out of range and silently skipped => output = [input[1]] = [6].
        using var block = new Projector(name: "P", indices: new[] { 1, 99 });
        var input = Vector<double>.Build.Dense(new[] { 5.0, 6.0, 7.0 });

        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(6.0, result[0], 1e-9); // input[1]
    }

    [TestMethod]
    public void OnReceive_MatrixColumnSelection_SelectsColumnsAndPreservesAllRows()
    {
        // input 3 rows × 3 cols:
        //   col0 = [1, 4, 7]ᵀ, col1 = [2, 5, 8]ᵀ, col2 = [3, 6, 9]ᵀ.
        // indices = [2, 0] => out col0 = input col2 = [3, 6, 9]ᵀ, out col1 = input col0 = [1, 4, 7]ᵀ.
        using var block = new Projector(name: "P", indices: new[] { 2, 0 });
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0, 3.0 },
            { 4.0, 5.0, 6.0 },
            { 7.0, 8.0, 9.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(3, result.RowCount);     // all timesteps preserved
        Assert.AreEqual(2, result.ColumnCount);  // two selected channels

        Assert.AreEqual(3.0, result[0, 0], 1e-9); // input col2, row0
        Assert.AreEqual(6.0, result[1, 0], 1e-9); // input col2, row1
        Assert.AreEqual(9.0, result[2, 0], 1e-9); // input col2, row2
        Assert.AreEqual(1.0, result[0, 1], 1e-9); // input col0, row0
        Assert.AreEqual(4.0, result[1, 1], 1e-9); // input col0, row1
        Assert.AreEqual(7.0, result[2, 1], 1e-9); // input col0, row2
    }

    [TestMethod]
    public void OnReceive_MatrixWithSomeInvalidColumnIndices_SkipsInvalidColumns()
    {
        // input 2 rows × 2 cols: col0 = [1, 3]ᵀ, col1 = [2, 4]ᵀ (valid range 0..1).
        // indices = [1, 5] => index 5 skipped => single output column = input col1 = [2, 4]ᵀ.
        using var block = new Projector(name: "P", indices: new[] { 1, 5 });
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0, 2.0 },
            { 3.0, 4.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(1, result.ColumnCount);
        Assert.AreEqual(2.0, result[0, 0], 1e-9); // input col1, row0
        Assert.AreEqual(4.0, result[1, 0], 1e-9); // input col1, row1
    }

    [TestMethod]
    public void OnReceive_VectorAllIndicesInvalid_PublishesNothing()
    {
        // input = [1, 2, 3] (valid range 0..2); indices = [7, 8] are all out of range.
        // With no valid index the block sets Stumbling and returns without publishing.
        using var block = new Projector(name: "P", indices: new[] { 7, 8 });
        var input = Vector<double>.Build.Dense(new[] { 1.0, 2.0, 3.0 });

        var fired = BlockHarness.TryCapture(block, input, out _);

        Assert.IsFalse(fired); // nothing published
    }
}
