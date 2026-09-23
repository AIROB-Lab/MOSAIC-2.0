using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Enums;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.SignalProcessing;

/// <summary>
/// Known-answer tests for <see cref="Crop"/>.
/// Matrix input (rows = samples, columns = channels/depth) is sliced to a sub-matrix.
///  - 2-param form (rowCount = 0 => HasRowCrop = false): keep ALL rows, crop columns
///    via SubMatrix(0, RowCount, DepthIndex, DepthCount).
///  - 4-param form (rowCount &gt; 0 => HasRowCrop = true): crop rows AND columns
///    via SubMatrix(RowIndex, RowCount, DepthIndex, DepthCount).
/// An out-of-range range makes MathNet's SubMatrix throw; OnReceive catches it, publishes
/// nothing, and reports a separate error while activity status remains automatic.
/// Values are integers exactly representable in double, so delta 1e-9 is used.
/// </summary>
[TestClass]
public class CropTests
{
    /// <summary>
    /// Reusable 3-row × 4-column source. Element value = 10*(row+1) + col, so every cell is
    /// unique and its (row, col) origin is obvious by inspection.
    /// </summary>
    private static Matrix<double> SourceMatrix() =>
        Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 10.0, 11.0, 12.0, 13.0 },
            { 20.0, 21.0, 22.0, 23.0 },
            { 30.0, 31.0, 32.0, 33.0 },
        });

    [TestMethod]
    public void OnReceive_ColumnOnlyCrop_KeepsAllRowsAndSelectedColumns()
    {
        // rowCount = 0 => HasRowCrop = false => SubMatrix(0, 3, DepthIndex=1, DepthCount=2).
        // Keep all 3 rows, columns 1..2:
        //   { 11, 12 }
        //   { 21, 22 }
        //   { 31, 32 }
        using var block = new Crop(name: "crop", desiredRate: 0, depthIndex: 1, depthCount: 2);

        var result = BlockHarness.CaptureMatrix(block, SourceMatrix());

        Assert.AreEqual(3, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);
        Assert.AreEqual(11.0, result[0, 0], 1e-9);
        Assert.AreEqual(12.0, result[0, 1], 1e-9);
        Assert.AreEqual(21.0, result[1, 0], 1e-9);
        Assert.AreEqual(22.0, result[1, 1], 1e-9);
        Assert.AreEqual(31.0, result[2, 0], 1e-9);
        Assert.AreEqual(32.0, result[2, 1], 1e-9);
    }

    [TestMethod]
    public void OnReceive_RowAndColumnCrop_ReturnsSubMatrix()
    {
        // rowCount = 2 (>0) => HasRowCrop = true =>
        //   SubMatrix(RowIndex=1, RowCount=2, DepthIndex=1, DepthCount=2).
        // Rows 1..2, columns 1..2:
        //   { 21, 22 }
        //   { 31, 32 }
        using var block = new Crop(
            name: "crop", desiredRate: 0, depthIndex: 1, depthCount: 2, rowIndex: 1, rowCount: 2);

        var result = BlockHarness.CaptureMatrix(block, SourceMatrix());

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);
        Assert.AreEqual(21.0, result[0, 0], 1e-9);
        Assert.AreEqual(22.0, result[0, 1], 1e-9);
        Assert.AreEqual(31.0, result[1, 0], 1e-9);
        Assert.AreEqual(32.0, result[1, 1], 1e-9);
    }

    [TestMethod]
    public void OnReceive_SingleCellCrop_ReturnsThatOneElement()
    {
        // SubMatrix(RowIndex=0, RowCount=1, DepthIndex=3, DepthCount=1) => the top-right corner.
        // Row 0, column 3 = 13.
        using var block = new Crop(
            name: "crop", desiredRate: 0, depthIndex: 3, depthCount: 1, rowIndex: 0, rowCount: 1);

        var result = BlockHarness.CaptureMatrix(block, SourceMatrix());

        Assert.AreEqual(1, result.RowCount);
        Assert.AreEqual(1, result.ColumnCount);
        Assert.AreEqual(13.0, result[0, 0], 1e-9);
    }

    [TestMethod]
    public void OnReceive_FullExtentCrop_ReproducesInputExactly()
    {
        // DepthIndex=0, DepthCount=4 covers every column; rowCount=0 keeps every row.
        // Output must equal the input element-for-element.
        using var block = new Crop(name: "crop", desiredRate: 0, depthIndex: 0, depthCount: 4);
        var input = SourceMatrix();

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(3, result.RowCount);
        Assert.AreEqual(4, result.ColumnCount);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                Assert.AreEqual(input[r, c], result[r, c], 1e-9);
    }

    [TestMethod]
    public void OnReceive_OutOfRangeColumnCrop_PublishesNothingAndReportsError()
    {
        // DepthIndex=2, DepthCount=5 => columns 2..6 requested from a 4-column matrix.
        // SubMatrix(0, 3, 2, 5) is out of range and throws inside OnReceive; the catch
        // handles it, so the block publishes nothing and reports an error (no clamping).
        using var block = new Crop(name: "crop", desiredRate: 0, depthIndex: 2, depthCount: 5);

        bool published = BlockHarness.TryCapture(block, SourceMatrix(), out _);

        Assert.IsFalse(published);
        Assert.IsNotNull(block.LastError);
        Assert.AreEqual(BlockStatus.Idle, block.Status);
    }

    [TestMethod]
    public void OnReceive_OutOfRangeRowCrop_PublishesNothingAndReportsError()
    {
        // RowIndex=1, RowCount=5 => rows 1..5 requested from a 3-row matrix.
        // SubMatrix(1, 5, 0, 2) is out of range and throws; caught => no publish, separate error message.
        using var block = new Crop(
            name: "crop", desiredRate: 0, depthIndex: 0, depthCount: 2, rowIndex: 1, rowCount: 5);

        bool published = BlockHarness.TryCapture(block, SourceMatrix(), out _);

        Assert.IsFalse(published);
        Assert.IsNotNull(block.LastError);
        Assert.AreEqual(BlockStatus.Idle, block.Status);
    }

    [TestMethod]
    public void Constructor_RowCountPositive_EnablesRowCrop()
    {
        // HasRowCrop is derived as (rowCount > 0) in the constructor.
        using var withRows = new Crop(name: "crop", rowIndex: 0, rowCount: 2);
        using var withoutRows = new Crop(name: "crop", rowIndex: 0, rowCount: 0);

        Assert.IsTrue(withRows.HasRowCrop);
        Assert.IsFalse(withoutRows.HasRowCrop);
    }
}
