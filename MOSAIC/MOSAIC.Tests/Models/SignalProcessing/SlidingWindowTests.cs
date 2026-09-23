using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.SignalProcessing;

/// <summary>
/// Known-answer tests for <see cref="SlidingWindow"/>.
///
/// The block buffers incoming rows and publishes a <see cref="Matrix{T}"/> of
/// <c>BufferSize × Channels</c> every <see cref="SlidingWindow.Stride"/> rows. On the first
/// data arrival it pre-fills the buffer with <c>BufferSize − 1</c> zero rows, so the very
/// first real sample completes (and publishes) the first window immediately. The harness
/// captures the FIRST published matrix, so with a fresh block the first window always
/// consists of <c>BufferSize − 1</c> zero rows followed by the earliest real row(s).
///
/// Each output row r is element-wise multiplied by the window coefficient w[r]:
///   Rectangular: w[r] = 1
///   Hamming:     w[r] = 0.54 − 0.46·cos(2π·r/(n−1))
///   Hann:        w[r] = 0.5·(1 − cos(2π·r/(n−1)))
/// where n = BufferSize.
/// </summary>
[TestClass]
public class SlidingWindowTests
{
    [TestMethod]
    public void ConfigureInput_PartialParametersUseDefaultsForOmittedEntries()
    {
        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        foreach (var parameters in new object[][] { [], [64], [64, 16] })
        {
            using var block = SlidingWindow.ConfigureInput(services,
                new MOSAIC.Components.Basics.JsonModel { Type = "SlidingWindow", Name = "window", Params = parameters });
            Assert.AreEqual(parameters.Length > 0 ? 64 : 100, block.BufferSize);
            Assert.AreEqual(parameters.Length > 1 ? 16 : 25, block.Stride);
            Assert.AreEqual(SlidingWindow.WindowType.Hamming, block.WindowKind);
        }
    }

    [TestMethod]
    public void OnReceive_FirstWindowRectangular_IsZeroPrefilledThenSample()
    {
        // bufferSize=3, stride=1. Feeding a single 1-channel sample [5] pre-fills 2 zero
        // rows, then appends the sample => buffer = [[0],[0],[5]] which is exactly 3 rows,
        // so the first (and captured) window is [[0],[0],[5]]. Rectangular => weights all 1.
        using var block = new SlidingWindow(name: "SW", bufferSize: 3, stride: 1,
                                            window: SlidingWindow.WindowType.Rectangular);
        var input = Matrix<double>.Build.DenseOfArray(new double[,] { { 5.0 } });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(3, result.RowCount);   // BufferSize rows
        Assert.AreEqual(1, result.ColumnCount); // one channel

        Assert.AreEqual(0.0, result[0, 0], 1e-9); // pre-filled zero row * 1
        Assert.AreEqual(0.0, result[1, 0], 1e-9); // pre-filled zero row * 1
        Assert.AreEqual(5.0, result[2, 0], 1e-9); // sample 5 * 1
    }

    [TestMethod]
    public void OnReceive_FirstWindowRectangular_MultiChannelShapeAndValues()
    {
        // bufferSize=2, stride=1, 3 channels. Feed one row [1,2,3]. Pre-fill 1 zero row,
        // append the sample => buffer = [[0,0,0],[1,2,3]] = 2 rows => first window.
        using var block = new SlidingWindow(name: "SW", bufferSize: 2, stride: 1,
                                            window: SlidingWindow.WindowType.Rectangular);
        var input = Matrix<double>.Build.DenseOfArray(new double[,] { { 1.0, 2.0, 3.0 } });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(3, result.ColumnCount);

        Assert.AreEqual(0.0, result[0, 0], 1e-9);
        Assert.AreEqual(0.0, result[0, 1], 1e-9);
        Assert.AreEqual(0.0, result[0, 2], 1e-9);
        Assert.AreEqual(1.0, result[1, 0], 1e-9); // channel 0 of the sample
        Assert.AreEqual(2.0, result[1, 1], 1e-9); // channel 1 of the sample
        Assert.AreEqual(3.0, result[1, 2], 1e-9); // channel 2 of the sample
    }

    [TestMethod]
    public void OnReceive_FeedingExactlyOneBufferInOneBatch_PublishesThatWindow()
    {
        // bufferSize=3, stride=3 (no overlap). Feed a 3-row batch [[1],[2],[3]] in ONE
        // ReceiveInput. Row 1 pre-fills 2 zeros => [[0],[0],[1]]; rows 2,3 append =>
        // [[0],[0],[1],[2],[3]] (5 rows). The while-loop emits exactly ONE window here
        // (5 >= 3 fires once, then removes stride=3 leaving 2 rows < 3), so the captured
        // window is unambiguously the earliest slice [[0],[0],[1]] (Rectangular => w=1).
        using var block = new SlidingWindow(name: "SW", bufferSize: 3, stride: 3,
                                            window: SlidingWindow.WindowType.Rectangular);
        var input = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { 1.0 },
            { 2.0 },
            { 3.0 },
        });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(3, result.RowCount);
        Assert.AreEqual(1, result.ColumnCount);

        Assert.AreEqual(0.0, result[0, 0], 1e-9); // pre-filled zero
        Assert.AreEqual(0.0, result[1, 0], 1e-9); // pre-filled zero
        Assert.AreEqual(1.0, result[2, 0], 1e-9); // first real sample
    }

    [TestMethod]
    public void OnReceive_HammingWindow_AppliesCoefficientsPerRow()
    {
        // bufferSize=3, Hamming: w[r] = 0.54 − 0.46·cos(2π·r/2).
        //   w[0] = 0.54 − 0.46·cos(0)  = 0.54 − 0.46 = 0.08
        //   w[1] = 0.54 − 0.46·cos(π)  = 0.54 + 0.46 = 1.00
        //   w[2] = 0.54 − 0.46·cos(2π) = 0.54 − 0.46 = 0.08
        // Feed one 1-channel sample [10] => buffer [[0],[0],[10]].
        // Output = [0·0.08, 0·1.0, 10·0.08] = [0, 0, 0.8].
        using var block = new SlidingWindow(name: "SW", bufferSize: 3, stride: 1,
                                            window: SlidingWindow.WindowType.Hamming);
        var input = Matrix<double>.Build.DenseOfArray(new double[,] { { 10.0 } });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(3, result.RowCount);
        Assert.AreEqual(1, result.ColumnCount);

        Assert.AreEqual(0.0, result[0, 0], 1e-6); // 0 * 0.08
        Assert.AreEqual(0.0, result[1, 0], 1e-6); // 0 * 1.00
        Assert.AreEqual(0.8, result[2, 0], 1e-6); // 10 * 0.08
    }

    [TestMethod]
    public void OnReceive_HannWindow_ZeroesEndpointsOfWindow()
    {
        // bufferSize=3, Hann: w[r] = 0.5·(1 − cos(2π·r/2)).
        //   w[0] = 0.5·(1 − cos(0))  = 0.5·(1 − 1) = 0
        //   w[1] = 0.5·(1 − cos(π))  = 0.5·(1 + 1) = 1
        //   w[2] = 0.5·(1 − cos(2π)) = 0.5·(1 − 1) = 0
        // Feed one 1-channel sample [7] => buffer [[0],[0],[7]].
        // Output = [0·0, 0·1, 7·0] = [0, 0, 0]  (the sample lands on a zero-weight endpoint).
        using var block = new SlidingWindow(name: "SW", bufferSize: 3, stride: 1,
                                            window: SlidingWindow.WindowType.Hann);
        var input = Matrix<double>.Build.DenseOfArray(new double[,] { { 7.0 } });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(3, result.RowCount);
        Assert.AreEqual(1, result.ColumnCount);

        Assert.AreEqual(0.0, result[0, 0], 1e-6); // 0 * 0
        Assert.AreEqual(0.0, result[1, 0], 1e-6); // 0 * 1 (middle weight, but sample is zero here)
        Assert.AreEqual(0.0, result[2, 0], 1e-6); // 7 * 0 (Hann endpoint weight is 0)
    }

    [TestMethod]
    public void OnReceive_VectorInput_IsAcceptedAsSingleRow()
    {
        // A Vector<double> input is treated as one row. bufferSize=2 => pre-fill 1 zero row,
        // append the vector [4,9] => buffer [[0,0],[4,9]] => first window (Rectangular).
        using var block = new SlidingWindow(name: "SW", bufferSize: 2, stride: 1,
                                            window: SlidingWindow.WindowType.Rectangular);
        var input = Vector<double>.Build.Dense(new[] { 4.0, 9.0 });

        var result = BlockHarness.CaptureMatrix(block, input);

        Assert.AreEqual(2, result.RowCount);
        Assert.AreEqual(2, result.ColumnCount);

        Assert.AreEqual(0.0, result[0, 0], 1e-9);
        Assert.AreEqual(0.0, result[0, 1], 1e-9);
        Assert.AreEqual(4.0, result[1, 0], 1e-9);
        Assert.AreEqual(9.0, result[1, 1], 1e-9);
    }

    [DataTestMethod]
    [DataRow(0, 1)]   // bufferSize <= 0
    [DataRow(-5, 1)]  // bufferSize <= 0
    [DataRow(4, 0)]   // stride <= 0
    [DataRow(4, 5)]   // stride > bufferSize
    public void Ctor_InvalidBufferOrStride_Throws(int bufferSize, int stride)
    {
        // bufferSize must be > 0; stride must be in [1, bufferSize].
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new SlidingWindow(name: "SW", bufferSize: bufferSize, stride: stride,
                              window: SlidingWindow.WindowType.Rectangular));
    }

    [TestMethod]
    public void OverlapPercent_ReflectsBufferAndStride()
    {
        // OverlapPercent = 100 − (Stride·100 / BufferSize), integer arithmetic.
        // bufferSize=4, stride=1 => 100 − (1·100/4) = 100 − 25 = 75.
        using var block = new SlidingWindow(name: "SW", bufferSize: 4, stride: 1,
                                            window: SlidingWindow.WindowType.Rectangular);

        Assert.AreEqual(75, block.OverlapPercent);
    }
}
