using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Models.Streaming;
using MOSAIC.ViewModels.SignalProcessing;

namespace MOSAIC.Tests.Components.Basics;

[TestClass]
public class PipelineContractTests
{
    private sealed class Source(double rate = 100) : BaseBlock("source", rate)
    {
        public override int MinInputs => 0;
        public void ChangeRate(double rate) { SignalRate = rate; UpdateAndPropagateRate(rate); }
        protected override void OnReceive(object sender, object value) { }
    }

    private sealed class Capture : ISubscriber
    {
        public readonly ConcurrentQueue<object> Values = new();
        public void ReceiveInput(object sender, object value) => Values.Enqueue(value);
    }

    [TestMethod]
    public async Task Resampler_IsIndependentOfPacketBoundaries()
    {
        using var vectors = new Source(10) { SignalRate = 10 };
        using var packets = new Source(2) { SignalRate = 10 };
        await using var a = new Resampler("vectors", 0, 4);
        await using var b = new Resampler("packets", 0, 4);
        var first = new Capture(); var second = new Capture();
        a.AddSubscriber(first); b.AddSubscriber(second);
        for (int i = 0; i < 10; i++) a.ReceiveInput(vectors, Vector<double>.Build.Dense(1, i));
        foreach (int offset in new[] { 0, 5 })
            b.ReceiveInput(packets, Matrix<double>.Build.Dense(5, 1, (r, _) => offset + r));
        await a.DisposeAsync(); await b.DisposeAsync();
        var expected = new double[] { 0, 2, 5, 7 };
        CollectionAssert.AreEqual(expected, first.Values.Cast<Vector<double>>().Select(v => v[0]).ToArray());
        CollectionAssert.AreEqual(expected, second.Values.Cast<Vector<double>>().Select(v => v[0]).ToArray());
    }

    [TestMethod]
    public async Task Resampler_UpsamplesWithStableHeldValuesAndOwnRate()
    {
        using var source = new Source(2) { SignalRate = 2 };
        await using var resampler = new Resampler("up", 0, 4);
        using var downstream = new Negate("downstream", 0);
        source.AddSubscriber(resampler); resampler.AddSubscriber(downstream);
        var capture = new Capture(); resampler.AddSubscriber(capture);
        foreach (double value in new[] { 10d, 20, 30 })
            resampler.ReceiveInput(source, Vector<double>.Build.Dense(1, value));
        await resampler.DisposeAsync();
        var output = capture.Values.Cast<Vector<double>>().ToArray();
        CollectionAssert.AreEqual(new double[] { 10, 10, 20, 20, 30 }, output.Select(v => v[0]).ToArray());
        Assert.AreNotSame(output[0], output[1]);
        Assert.AreEqual(4d, downstream.DesiredRate);
        Assert.AreEqual(4d, downstream.SignalRate);
    }

    [TestMethod]
    public void RateChanges_ReachWindowsAndPassThroughsWithoutOverwritingResampler()
    {
        using var source = new Source(100) { SignalRate = 100 };
        using var window = new SlidingWindow("window", 100, 25);
        using var feature = new Negate("feature", 0);
        using var resampler = new Resampler("resampler", 0, 10);
        using var end = new Negate("end", 0);
        source.AddSubscriber(window); window.AddSubscriber(feature);
        feature.AddSubscriber(resampler); resampler.AddSubscriber(end);
        foreach (double rate in new[] { 200d, 50d, 100d })
        {
            source.ChangeRate(rate);
            Assert.AreEqual(rate / 25, window.DesiredRate);
            Assert.AreEqual(rate / 25, feature.DesiredRate);
            Assert.AreEqual(rate, feature.SignalRate);
            Assert.AreEqual(10d, resampler.DesiredRate);
            Assert.AreEqual(10d, end.SignalRate);
        }
    }

    [TestMethod]
    public void OpeningFunctionCard_DoesNotReplaceFeatureCadenceWithAcquisitionRate()
    {
        using var source = new Source(4) { SignalRate = 256 };
        using var function = new Function("features", 0, value => value * 2);
        source.AddSubscriber(function);
        using var card = new FunctionViewModel(function);
        function.ReceiveInput(source, Vector<double>.Build.Dense(1, 3));
        Assert.AreEqual(4d, card.Viz.EffectiveSignalRate);
        Assert.AreEqual(0d, card.Viz.SignalRate, "Card creation must not install a DSP-rate override.");
    }

    [TestMethod]
    public void Filter_AppliesWholeBandBeforeNotificationsAndRejectsInvalidDraft()
    {
        using var filter = new Filter("band", 1000, FilterType.Bandpass, cutoffLow: 20, cutoffHigh: 50);
        var observed = new List<(double, double)>();
        filter.PropertyChanged += (_, _) => observed.Add((filter.CutoffLow, filter.CutoffHigh));
        filter.SetBand(100, 200);
        Assert.IsNotEmpty(observed);
        Assert.IsTrue(observed.All(pair => pair == (100, 200)));
        Assert.ThrowsExactly<ArgumentException>(() => filter.SetBand(300, 200));
        Assert.AreEqual(100d, filter.CutoffLow);
        Assert.AreEqual(200d, filter.CutoffHigh);
    }

    [TestMethod]
    public async Task SinGenerator_ChannelEditsCannotResizeAnActivePacket()
    {
        await using var generator = new SinGenerator(desiredRate: 100, componentCount: 2, scansPerPacket: 4);
        var capture = new Capture(); generator.AddSubscriber(capture);
        using var start = new ManualResetEventSlim(false);
        var edits = Task.Run(() => { start.Wait(); for (int i = 0; i < 1000; i++) generator.ComponentCount = i % 2 + 1; });
        var input = Task.Run(() => { start.Wait(); for (int i = 0; i < 1000; i++) generator.ReceiveInput(this, 0d); });
        start.Set();
        await Task.WhenAll(edits, input);
        await generator.DisposeAsync();
        Assert.HasCount(1000, capture.Values);
        Assert.IsTrue(capture.Values.Cast<Matrix<double>>().All(m => m.RowCount == 4 && m.ColumnCount is 1 or 2));
    }

    [TestMethod]
    public void FilterCard_RejectsInvalidDraftWithoutChangingAppliedSettings()
    {
        using var filter = new Filter("band", 1000, FilterType.Bandpass, cutoffLow: 20, cutoffHigh: 50);
        using var card = new FilterViewModel(filter);
        card.CutoffLow = 100;
        card.CutoffHigh = 50;
        card.ApplyChangesCommand.Execute(null);
        Assert.AreEqual(20d, filter.CutoffLow);
        Assert.AreEqual(50d, filter.CutoffHigh);
        StringAssert.Contains(filter.LastError!, "not applied");
        card.CutoffHigh = 200;
        card.ApplyChangesCommand.Execute(null);
        Assert.AreEqual(100d, filter.CutoffLow);
        Assert.AreEqual(200d, filter.CutoffHigh);
        Assert.IsNull(filter.LastError);
    }

    [TestMethod]
    public void SlidingWindow_CatalogueDefaultsAndEveryChoiceCanBeLoaded()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var descriptor = BlockCatalog.ByKey["slidingwindow"];
        using var defaults = new SlidingWindow("defaults");
        using var jsonDefaults = SlidingWindow.ConfigureInput(services, new JsonModel { Type = "slidingwindow" });
        foreach (var choice in descriptor.Params[2].Choices!)
        {
            using var block = SlidingWindow.ConfigureInput(services, new JsonModel
            {
                Type = "slidingwindow", Params = [descriptor.Params[0].Default!, descriptor.Params[1].Default!, choice]
            });
            Assert.AreEqual(defaults.BufferSize, block.BufferSize);
            Assert.AreEqual(defaults.Stride, block.Stride);
            Assert.AreEqual(choice, block.WindowKind.ToString());
        }
        Assert.AreEqual(defaults.BufferSize, jsonDefaults.BufferSize);
        Assert.AreEqual(defaults.Stride, jsonDefaults.Stride);
        Assert.AreEqual(defaults.WindowKind, jsonDefaults.WindowKind);
        Assert.AreEqual(defaults.WindowKind.ToString(), descriptor.Params[2].Default);
    }

    [TestMethod]
    public void FaultReportingAndRecovery_DoNotAssignActivityStatus()
    {
        using var block = new Source();
        block.ReportError("Invalid input");
        Assert.AreEqual(BlockStatus.Idle, block.Status);
        Assert.AreEqual("Invalid input", block.LastError);
        Assert.IsTrue(block.HasDiagnostics);
        block.ClearError();
        Assert.IsFalse(block.HasDiagnostics);
        Assert.AreEqual(BlockStatus.Idle, block.Status);
    }

    private sealed class BlockingSubscriber : ISubscriber, IDisposable
    {
        public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public readonly List<object> Values = new();
        public void ReceiveInput(object sender, object value)
        {
            Entered.Set(); Release.Wait(); Values.Add(value);
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class ThrowingBlock : BaseBlock
    {
        public ThrowingBlock() : base("broken") { }
        protected override void OnReceive(object sender, object value) => throw new InvalidOperationException("Bad sample");
    }

    [TestMethod]
    public async Task SubscriberFailure_IsVisibleAndDoesNotStopOtherBranches()
    {
        await using var dispatcher = new OutputDispatcher();
        using var broken = new ThrowingBlock();
        var healthy = new Capture();
        dispatcher.AddSubscriber(broken); dispatcher.AddSubscriber(healthy);
        dispatcher.Dispatch(this, 1);
        dispatcher.Dispatch(this, 2);
        await dispatcher.DisposeAsync();
        Assert.AreEqual(2L, dispatcher.SubscriberFailures);
        Assert.HasCount(2, healthy.Values);
        StringAssert.Contains(broken.LastError!, "Bad sample");
        Assert.IsTrue(broken.HasDiagnostics);
    }

    [TestMethod]
    public async Task ProcessingOverflow_BoundsQueueAndStopsInsteadOfSkippingAGap()
    {
        await using var dispatcher = new OutputDispatcher(queueCapacity: 2);
        using var subscriber = new BlockingSubscriber();
        dispatcher.AddSubscriber(subscriber);
        dispatcher.Dispatch(this, 0);
        Assert.IsTrue(subscriber.Entered.Wait(3000));
        try
        {
            foreach (int value in new[] { 1, 2, 3, 4 }) dispatcher.Dispatch(this, value);
            Assert.AreEqual(2, dispatcher.QueuedValues);
            Assert.AreEqual(2L, dispatcher.RejectedValues);
        }
        finally { subscriber.Release.Set(); }
        await dispatcher.DisposeAsync();
        CollectionAssert.AreEqual(new object[] { 0, 1, 2 }, subscriber.Values);
    }

    [TestMethod]
    public async Task RecordingPressure_AccountsForEveryRowAndDoesNotLoseFlushBarriers()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mosaic-pressure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var dumper = new CsvDumper(directory, "rows", queueCapacity: 2);
            const int count = 20000;
            var producer = Task.Run(() => { for (int i = 0; i < count; i++) dumper.Enqueue(i, (double)i); });
            for (int i = 0; i < 20; i++) await dumper.FlushAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await producer;
            await dumper.DisposeAsync();
            var rows = File.ReadAllLines(Path.Combine(directory, "rows.csv"));
            Assert.AreEqual((long)count, rows.LongLength + dumper.DroppedRows);
            Assert.IsNull(dumper.LastError);
            var values = rows.Select(line => int.Parse(line.Split(',')[1])).ToArray();
            Assert.IsTrue(values.Zip(values.Skip(1), (a, b) => a < b).All(ordered => ordered));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task RecordingOpenFailure_CompletesFlushWithTheError()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mosaic-failed-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var lockedFile = new FileStream(Path.Combine(directory, "rows.csv"), FileMode.Create, FileAccess.Write, FileShare.None);
            await using var dumper = new CsvDumper(directory, "rows", queueCapacity: 2);
            dumper.Enqueue(0, 1d);
            try
            {
                await dumper.FlushAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail("A failed recording must not report a successful flush.");
            }
            catch (IOException) { }
            Assert.IsInstanceOfType<IOException>(dumper.LastError);
        }
        finally { Directory.Delete(directory, true); }
    }
}
