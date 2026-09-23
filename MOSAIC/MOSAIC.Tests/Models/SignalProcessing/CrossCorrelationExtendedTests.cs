using System;
using System.Linq;
using System.Text.Json;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Tests.TestSupport;
using MOSAIC.ViewModels.SignalProcessing;

namespace MOSAIC.Tests.Models.SignalProcessing;

[TestClass]
public class CrossCorrelationExtendedTests
{
    private static CrossCorrelation Create(params object[] options)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var model = JsonSerializer.Deserialize<JsonModel>(JsonSerializer.Serialize(new JsonModel
        {
            Type = "CrossCorrelation", Name = "CC", DesiredRate = 50,
            Params = options.Length > 0 ? options : ["Lags:1:36", 1000, 1, 4, "LowPass:3", "HighPass:70", "MovMedian:5"]
        }))!;
        return CrossCorrelation.ConfigureInput(services, model);
    }

    internal static Matrix<double> FourChannels(int count = 1000)
    {
        var random = new Random(42);
        var signal = Enumerable.Range(0, count + 8).Select(_ => (double)random.Next(1000, 5000)).ToArray();
        return Matrix<double>.Build.Dense(count, 4, (r, c) => c switch
        {
            0 => signal[r], 1 => signal[r + 8], 2 => 40000, _ => 65000
        });
    }

    [TestMethod]
    public void UserParametersProducePeakLagAndRateInsteadOfACurve()
    {
        using var block = Create();
        Assert.IsTrue(block.UsesLagSummary);
        Assert.AreEqual(1, block.MinLag);
        Assert.AreEqual(36, block.MaxLag);
        Assert.AreEqual(4, block.ChannelCount);
        Assert.AreEqual(116, block.RequiredSamples);
        var output = BlockHarness.CaptureVector(block, FourChannels());
        Assert.AreEqual(3, output.Count);
        Assert.AreEqual(8, output[0], 0);
        Assert.AreEqual(6.25, output[1], 1e-12);
        Assert.AreEqual(0, output[2], 0);
    }

    [TestMethod]
    public void FourChannelInterleavedPacketsMatchMatrices()
    {
        var input = FourChannels();
        using var matrix = Create();
        using var vector = Create();
        var interleaved = Vector<double>.Build.Dense(input.RowCount * 4, i => input[i / 4, i % 4]);
        CollectionAssert.AreEqual(BlockHarness.CaptureVector(matrix, input).ToArray(),
            BlockHarness.CaptureVector(vector, interleaved).ToArray());
    }

    [TestMethod]
    public void ConfiguredFiltersMatchAnIndependentReferenceIncludingMedian()
    {
        using var block = Create();
        var input = FourChannels().Column(0);
        double mean = input.Average();
        double std = Math.Sqrt(input.Average(v => (v - mean) * (v - mean)));
        var normalized = input.Select(v => (v - mean) / std).ToArray();
        var low = Enumerable.Range(0, normalized.Length - 3).Select(i => normalized.Skip(i).Take(3).Average()).ToArray();
        var median = Enumerable.Range(0, low.Length - 5).Select(i => low.Skip(i).Take(5).Order().ElementAt(2)).ToArray();
        var expected = Enumerable.Range(0, median.Length - 70).Select(i => median[i] - median.Skip(i).Take(70).Average()).ToArray();
        var actual = block.ProcessConfigured(input);
        Assert.AreEqual(expected.Length, actual.Count);
        for (int i = 0; i < actual.Count; i++) Assert.AreEqual(expected[i], actual[i], 1e-10);
    }

    [TestMethod]
    public void EvenMedianWindowAveragesTheMiddleTwoValues()
    {
        using var block = Create("Lags:1:2", 100, 1, 2, "MovMedian:4");
        var input = Vector<double>.Build.DenseOfArray([7, 1, 4, 9, 3, 8, 2, 6]);
        double mean = input.Average(), std = Math.Sqrt(input.Average(v => (v - mean) * (v - mean)));
        var result = block.ProcessConfigured(input);
        Assert.AreEqual(((4d + 7d) / 2 - mean) / std, result[0], 1e-12);
    }

    [TestMethod]
    public void StreamingWarmupUsesConfiguredWindowsAndLagRange()
    {
        using var block = Create();
        var input = FourChannels();
        for (int i = 0; i < 115; i++) block.ReceiveInput(this, input.Row(i));
        Assert.AreEqual(0L, block.UpdatesComputed);
        StringAssert.Contains(block.StatusMessage, "115/116");
        Assert.IsTrue(BlockHarness.CaptureVector(block, input.Row(115)).All(double.IsFinite));
        Assert.AreEqual(1L, block.UpdatesComputed);
    }

    [TestMethod]
    public void ZeroLagAndFlatInputCannotPublishInfinityOrAFakeRate()
    {
        using var zero = Create("Lags:0:0", 1000, 1, 4);
        var input = FourChannels();
        input.SetColumn(1, input.Column(0));
        var zeroOutput = BlockHarness.CaptureVector(zero, input);
        Assert.IsTrue(zeroOutput.All(v => v == 0));
        StringAssert.Contains(zero.StatusMessage, "zero lag");
        using var flat = Create();
        Assert.IsTrue(BlockHarness.CaptureVector(flat, Matrix<double>.Build.Dense(1000, 4, 1)).All(v => v == 0));
    }

    [TestMethod]
    public void SignedLagRangeReturnsItsActualOffset()
    {
        using var block = Create("Lags:-36:-1", 1000, 1, 4, "LowPass:3", "HighPass:70", "MovMedian:5", "Channels:1:0");
        var result = BlockHarness.CaptureVector(block, FourChannels());
        Assert.AreEqual(-8, result[0], 0);
        Assert.AreEqual(-6.25, result[1], 1e-12);
    }

    [TestMethod]
    public void FullLagRangeUsesTheTrueFirstBinOffset()
    {
        using var block = Create("Lags:0", 1000, 1, 4);
        Assert.AreEqual(8, BlockHarness.CaptureVector(block, FourChannels())[0], 0);
    }

    [TestMethod]
    public void ChangingChannelsClearsOldSamplesAndPersistsSelections()
    {
        using var block = Create();
        using var vm = new CrossCorrelationViewModel(block);
        Assert.AreEqual("Ch 0", vm.SelectedChannelOption1);
        Assert.AreEqual("Ch 1", vm.SelectedChannelOption2);
        block.ReceiveInput(this, FourChannels());
        vm.SelectedChannelOption1 = "Ch 2";
        vm.SelectedChannelOption2 = "All (avg)";
        block.ReceiveInput(this, FourChannels().Row(0));
        Assert.AreEqual(1L, block.UpdatesComputed);
        StringAssert.Contains(block.StatusMessage, "1/116");
        using var services = new ServiceCollection().BuildServiceProvider();
        var saved = JsonSerializer.Deserialize<JsonModel>(JsonSerializer.Serialize(block.ToJsonModel()))!;
        using var reloaded = CrossCorrelation.ConfigureInput(services, saved);
        Assert.AreEqual(2, reloaded.SelectedChannel1);
        Assert.AreEqual(-1, reloaded.SelectedChannel2);
        Assert.AreEqual(4, reloaded.ChannelCount);
        Assert.AreEqual(3, reloaded.LowPassWindow);
        Assert.AreEqual(70, reloaded.HighPassWindow);
        Assert.AreEqual(5, reloaded.MovMedianWindow);
        Assert.AreEqual(1, reloaded.MinLag);
        Assert.AreEqual(36, reloaded.MaxLag);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => block.SelectedChannel1 = 4);
    }

    [TestMethod]
    public void AverageSelectorReadsAllChannelsWithoutNegativeIndexing()
    {
        using var block = Create("Lags:0:0", 1000, 1, 4, "Channels:-1:-1");
        Assert.IsTrue(BlockHarness.CaptureVector(block, FourChannels()).All(double.IsFinite));
    }

    [TestMethod]
    public void InvalidExtendedParametersFailClearlyInsteadOfBeingIgnored()
    {
        object[][] invalid =
        [
            ["Lags:36:1", 1000, 1, 4], ["Lags:abc", 1000, 1, 4],
            ["Lags:1:36", 1000, 1, 0], ["Lags:1:36", 1000, 1, 4, "LowPass:0"],
            ["Lags:1:36", 1000, 1, 4, "Typo:5"], ["Lags:1:36", 1000, 1, 4, "Channels:0:4"],
            ["Lags:1:36", 1000, 1, 4, "MovMedian:3", "MovMedian:5"]
        ];
        foreach (var parameters in invalid)
            Assert.ThrowsExactly<ArgumentException>(() => { using var block = Create(parameters); });
    }
}
