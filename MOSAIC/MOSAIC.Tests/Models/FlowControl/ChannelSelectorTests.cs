using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.FlowControl;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Known-answer tests for <see cref="ChannelSelector"/>.
///
/// The block treats matrix columns as channels (rows = timesteps). On the first input it
/// detects the channel count and marks every channel active. The activation mask is then
/// applied per column:
/// <list type="bullet">
///   <item><description>Zero mode (DeleteChannel = false): inactive columns are zeroed, dimensions unchanged.</description></item>
///   <item><description>Delete mode (DeleteChannel = true): inactive columns are removed, kept columns stay in ascending source order.</description></item>
/// </list>
///
/// Because the very first input resets the activation array to all-active (via the internal
/// channel-count detection), each test primes the channel count with one feed, then sets the
/// mask, then feeds again and asserts on that second output. A second feed with the same column
/// count preserves the mask.
/// </summary>
[TestClass]
public class ChannelSelectorTests
{
    // rows = timesteps, cols = channels:  col0 = [1,4], col1 = [2,5], col2 = [3,6]
    private static Matrix<double> ThreeChannelInput() => Matrix<double>.Build.DenseOfArray(new double[,]
    {
        { 1.0, 2.0, 3.0 },
        { 4.0, 5.0, 6.0 },
    });

    [TestMethod]
    public void OnReceive_AllChannelsActiveByDefault_ReturnsInputUnchanged()
    {
        // First matrix seen => every one of the 3 channels is active. Zero mode is the default,
        // and with nothing masked the output equals the input, same 2x3 shape.
        using var block = new ChannelSelector(name: "CS", desiredRate: 0);
        var input = ThreeChannelInput();

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(3, result.ColumnCount);
        Assert.AreEqual(1.0, result[0, 0], 1e-9);
        Assert.AreEqual(2.0, result[0, 1], 1e-9);
        Assert.AreEqual(3.0, result[0, 2], 1e-9);
        Assert.AreEqual(4.0, result[1, 0], 1e-9);
        Assert.AreEqual(5.0, result[1, 1], 1e-9);
        Assert.AreEqual(6.0, result[1, 2], 1e-9);
    }

    [TestMethod]
    public void OnReceive_ZeroMode_InactiveColumnIsZeroedShapePreserved()
    {
        // Zero mode, channel 1 disabled => middle column becomes 0, shape stays 2x3.
        // Expected: [[1,0,3],[4,0,6]].
        using var block = new ChannelSelector(name: "CS", desiredRate: 0) { DeleteChannel = false };
        var input = ThreeChannelInput();

        BlockHarness.CaptureMatrix(block, input); // prime channel count to 3 (all active)
        block.UpdateChannelActivations(new[] { true, false, true });
        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(3, result.ColumnCount);
        Assert.AreEqual(1.0, result[0, 0], 1e-9);
        Assert.AreEqual(0.0, result[0, 1], 1e-9); // masked channel
        Assert.AreEqual(3.0, result[0, 2], 1e-9);
        Assert.AreEqual(4.0, result[1, 0], 1e-9);
        Assert.AreEqual(0.0, result[1, 1], 1e-9); // masked channel
        Assert.AreEqual(6.0, result[1, 2], 1e-9);
    }

    [TestMethod]
    public void OnReceive_DeleteMode_InactiveColumnsRemoved()
    {
        // Delete mode, channels 0 and 2 kept, channel 1 removed => 2x2 of columns [0,2].
        // Expected: [[1,3],[4,6]].
        using var block = new ChannelSelector(name: "CS", desiredRate: 0) { DeleteChannel = true };
        var input = ThreeChannelInput();

        BlockHarness.CaptureMatrix(block, input); // prime channel count to 3 (all active)
        block.UpdateChannelActivations(new[] { true, false, true });
        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);
        Assert.AreEqual(1.0, result[0, 0], 1e-9); // original channel 0
        Assert.AreEqual(3.0, result[0, 1], 1e-9); // original channel 2
        Assert.AreEqual(4.0, result[1, 0], 1e-9);
        Assert.AreEqual(6.0, result[1, 1], 1e-9);
    }

    [TestMethod]
    public void OnReceive_DeleteMode_KeepsAscendingSourceOrder()
    {
        // Delete mode dropping the FIRST channel keeps channels 1 and 2 in ascending order.
        // Expected: [[2,3],[5,6]].
        using var block = new ChannelSelector(name: "CS", desiredRate: 0) { DeleteChannel = true };
        var input = ThreeChannelInput();

        BlockHarness.CaptureMatrix(block, input); // prime channel count to 3 (all active)
        block.UpdateChannelActivations(new[] { false, true, true });
        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);
        Assert.AreEqual(2.0, result[0, 0], 1e-9); // original channel 1
        Assert.AreEqual(3.0, result[0, 1], 1e-9); // original channel 2
        Assert.AreEqual(5.0, result[1, 0], 1e-9);
        Assert.AreEqual(6.0, result[1, 1], 1e-9);
    }

    [TestMethod]
    public void OnReceive_DeleteMode_AllChannelsInactive_ProducesZeroWidthMatrix()
    {
        // Delete mode with no active channels => guard returns a rows x 0 matrix.
        using var block = new ChannelSelector(name: "CS", desiredRate: 0) { DeleteChannel = true };
        var input = ThreeChannelInput();

        BlockHarness.CaptureMatrix(block, input); // prime channel count to 3 (all active)
        block.UpdateChannelActivations(new[] { false, false, false });
        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(0, result.ColumnCount);
    }

    [TestMethod]
    public void OnReceive_VectorInput_ZeroMode_MasksElementInPlace()
    {
        // Vector input: each element is a channel. Zero mode with element 1 disabled zeros that
        // slot while keeping length 3. Input [10,20,30] => [10,0,30].
        using var block = new ChannelSelector(name: "CS", desiredRate: 0) { DeleteChannel = false };
        var input = Vector<double>.Build.Dense(new[] { 10.0, 20.0, 30.0 });

        BlockHarness.CaptureVector(block, input); // prime channel count to 3 (all active)
        block.UpdateChannelActivations(new[] { true, false, true });
        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(10.0, result[0], 1e-9);
        Assert.AreEqual(0.0, result[1], 1e-9); // masked element
        Assert.AreEqual(30.0, result[2], 1e-9);
    }

    [TestMethod]
    public void OnReceive_VectorInput_DeleteMode_RemovesInactiveElements()
    {
        // Vector input, delete mode: inactive elements are dropped, active ones keep order.
        // Input [10,20,30] with element 1 disabled => [10,30], length 2.
        using var block = new ChannelSelector(name: "CS", desiredRate: 0) { DeleteChannel = true };
        var input = Vector<double>.Build.Dense(new[] { 10.0, 20.0, 30.0 });

        BlockHarness.CaptureVector(block, input); // prime channel count to 3 (all active)
        block.UpdateChannelActivations(new[] { true, false, true });
        var result = BlockHarness.CaptureVector(block, input);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(10.0, result[0], 1e-9); // original element 0
        Assert.AreEqual(30.0, result[1], 1e-9); // original element 2
    }
}
