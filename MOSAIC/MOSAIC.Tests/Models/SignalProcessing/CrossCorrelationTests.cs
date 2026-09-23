using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.SignalProcessing;

[TestClass]
public class CrossCorrelationTests
{
    private static CrossCorrelation Block(int lag = 20, int buffer = 1000, int every = 1)
        => new("xcorr", 0, lag, buffer, every);

    private static Matrix<double> Input(int samples)
        => Matrix<double>.Build.Dense(samples, 2, (r, c) =>
        {
            double t = r - 4 * c;
            return Math.Sin(t * 0.027) + 0.35 * Math.Cos(t * 0.133) + 0.1 * Math.Sin(t * 0.47);
        });

    private static void Equal(Vector<double> expected, Vector<double> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        for (int i = 0; i < actual.Count; i++) Assert.AreEqual(expected[i], actual[i], 1e-8, $"Lag bin {i}");
    }

    [TestMethod]
    public void KernelDelayedSecondChannelHasNegativeLag()
    {
        var x = Vector<double>.Build.DenseOfArray([0, 1, 0, 0, 0]);
        var y = Vector<double>.Build.DenseOfArray([0, 0, 0, 1, 0]);
        Equal(Vector<double>.Build.DenseOfArray([0, 10, 0, 0, 0, 0, 0]),
            CrossCorrelation.ComputeCrossCorrelation(x, y, 3));
    }

    [TestMethod]
    public void KernelFullRangeHasExpectedOverlapSumsAndScaling()
    {
        var x = Vector<double>.Build.DenseOfArray([1, 2, 3]);
        var y = Vector<double>.Build.DenseOfArray([4, 5]);
        var expected = Vector<double>.Build.DenseOfArray([5, 14, 23, 12]);
        expected = expected / expected.L2Norm() * 10;
        Equal(expected, CrossCorrelation.ComputeCrossCorrelation(x, y, 0));
    }

    [TestMethod]
    public void MatrixPipelinePreservesFeatureBranchProcessingFormula()
    {
        using var block = Block();
        var input = Input(650);
        var actual = BlockHarness.CaptureVector(block, input);
        // Independent array reference for the original 10-point max / 500-point mean stages.
        double[] Process(double[] values)
        {
            double mean = values.Average();
            double std = Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
            var normalized = values.Select(v => (v - mean) / std).ToArray();
            var envelope = Enumerable.Range(0, values.Length - 10)
                .Select(i => normalized.Skip(i).Take(10).Max()).ToArray();
            return Enumerable.Range(0, envelope.Length - 500)
                .Select(i => envelope[i] - envelope.Skip(i).Take(500).Average()).ToArray();
        }
        var x = Process(input.Column(0).ToArray());
        var y = Process(input.Column(1).ToArray());
        var sums = Enumerable.Range(-20, 41).Select(lag =>
            Enumerable.Range(0, x.Length).Where(i => i - lag >= 0 && i - lag < y.Length)
                .Sum(i => x[i] * y[i - lag])).ToArray();
        var expected = Vector<double>.Build.DenseOfArray(sums);
        Equal(expected / expected.L2Norm() * 10, actual);
        Assert.AreEqual(1L, block.UpdatesComputed);
        Assert.IsFalse(block.InputFlat);
    }

    [TestMethod]
    public void InterleavedVectorMatchesTwoColumnMatrix()
    {
        using var matrixBlock = Block();
        using var vectorBlock = Block();
        var input = Input(650);
        var interleaved = Vector<double>.Build.Dense(input.RowCount * 2, i => input[i / 2, i % 2]);
        Equal(BlockHarness.CaptureVector(matrixBlock, input), BlockHarness.CaptureVector(vectorBlock, interleaved));
    }

    [TestMethod]
    public void SmallPacketsWarmUpInsteadOfThrowingOrPublishingEmptyData()
    {
        using var block = Block(lag: 3);
        var input = Input(CrossCorrelation.MinimumBufferLength);
        for (int i = 0; i < input.RowCount - 1; i++) block.ReceiveInput(this, input.Row(i));
        Assert.AreEqual(0L, block.UpdatesComputed);
        StringAssert.Contains(block.StatusMessage, "Warming up");
        var result = BlockHarness.CaptureVector(block, input.Row(input.RowCount - 1));
        Assert.AreEqual(7, result.Count);
        Assert.IsTrue(result.All(double.IsFinite));
        Assert.AreEqual(1L, block.UpdatesComputed);
    }

    [TestMethod]
    public void OversizedPacketCorrelatesOnlyTheLatestBufferWindow()
    {
        using var oversized = Block(lag: 0, buffer: 600);
        using var expected = Block(lag: 0, buffer: 600);
        var input = Input(1200);
        var actual = BlockHarness.CaptureVector(oversized, input);
        Equal(BlockHarness.CaptureVector(expected, input.SubMatrix(600, 600, 0, 2)), actual);
        Assert.AreEqual(2 * (600 - 10 - 500) - 1, actual.Count);
    }

    [TestMethod]
    public void FlatInputReportsWarningAndProducesFiniteZeros()
    {
        using var block = Block(lag: 8);
        var result = BlockHarness.CaptureVector(block, Matrix<double>.Build.Dense(650, 2, 4));
        Assert.IsTrue(block.InputFlat);
        StringAssert.Contains(block.StatusMessage, "Flat input");
        Assert.IsTrue(result.All(v => v == 0));
    }

    [TestMethod]
    public void UnsupportedPayloadsAreIgnored()
    {
        using var block = Block();
        block.ReceiveInput(this, Matrix<double>.Build.Dense(650, 1));
        block.ReceiveInput(this, Vector<double>.Build.Dense(3));
        block.ReceiveInput(this, "not a signal");
        Assert.AreEqual(0L, block.UpdatesComputed);
    }

    [TestMethod]
    public void ProcessEveryNCountsAcceptedPackets()
    {
        using var block = Block(every: 2);
        block.ReceiveInput(this, Input(650));
        Assert.AreEqual(0L, block.UpdatesComputed);
        block.ReceiveInput(this, "ignored");
        Assert.AreEqual(0L, block.UpdatesComputed);
        BlockHarness.CaptureVector(block, Input(650));
        Assert.AreEqual(1L, block.UpdatesComputed);
    }

    [TestMethod]
    public void ConfigurationBoundsAreSafeAtConstructionAndAtRuntime()
    {
        using var block = Block(lag: -1, buffer: 2, every: 0);
        Assert.AreEqual(0, block.MaxLag);
        Assert.AreEqual(CrossCorrelation.MinimumBufferLength, block.BufferLen);
        Assert.AreEqual(1, block.ProcessEveryN);
        block.MaxLag = -2;
        block.BufferLen = -2;
        block.ProcessEveryN = -2;
        Assert.AreEqual(0, block.MaxLag);
        Assert.AreEqual(CrossCorrelation.MinimumBufferLength, block.BufferLen);
        Assert.AreEqual(1, block.ProcessEveryN);
        Assert.IsTrue(BlockHarness.CaptureVector(block, Input(650)).All(double.IsFinite));
    }

    [TestMethod]
    public void FactoryPaletteAndJsonRoundTripUseCurrentRegistrations()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = new BlockFactory(services);
        using var block = (CrossCorrelation)factory.Create(new JsonModel
        {
            Type = "CrossCorrelation", Name = "test-xcorr", DesiredRate = 25, Params = [7, 650, 3]
        });
        var model = JsonSerializer.Deserialize<JsonModel>(JsonSerializer.Serialize(block.ToJsonModel()))!;
        using var reloaded = (CrossCorrelation)factory.Create(model);
        Assert.AreEqual("test-xcorr", reloaded.Name);
        Assert.AreEqual(7, reloaded.MaxLag);
        Assert.AreEqual(650, reloaded.BufferLen);
        Assert.AreEqual(3, reloaded.ProcessEveryN);
        Assert.AreEqual(25, reloaded.DesiredRate);
        Assert.AreEqual(typeof(CrossCorrelation), BlockCatalog.ByKey["crosscorrelation"].BlockType);
        Assert.AreEqual(1, BlockConstraints.For(typeof(CrossCorrelation)).Min);
        Assert.AreEqual(1, BlockConstraints.For(typeof(CrossCorrelation)).Max);
    }

    [TestMethod]
    public async Task FactoryRecordingAndExplicitPathSidecarsStopTogether()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mosaic_xcorr_" + Guid.NewGuid().ToString("N"));
        using var services = new ServiceCollection().BuildServiceProvider();
        try
        {
            using var block = (CrossCorrelation)new BlockFactory(services).Create(new JsonModel
            {
                Type = "CrossCorrelation", Name = "corr", Path = folder, Params = [4, 650, 1]
            });
            Assert.IsTrue(block.IsRecording);
            BlockHarness.CaptureVector(block, Input(650));
            block.IsRecording = false;
            BlockHarness.CaptureVector(block, Input(650));
            Assert.AreEqual(folder, block.ToJsonModel().Path);
            block.Dispose();
            foreach (string name in new[] { "corr_xcorr.csv", "corr_ch1.csv", "corr_ch2.csv" })
            {
                string file = Path.Combine(folder, name);
                string[] rows = [];
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    try { rows = File.ReadAllLines(file); } catch (IOException) { }
                    if (rows.Length > 0) break;
                    await Task.Delay(20);
                }
                Assert.HasCount(1, rows, name + " must contain only the frame recorded before Stop.");
            }
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void DisposeIsIdempotentAndStopsFurtherFrames()
    {
        using var block = Block();
        block.Dispose();
        block.Dispose();
        block.ReceiveInput(this, Input(650));
        Assert.AreEqual(0L, block.UpdatesComputed);
    }
}
