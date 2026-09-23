using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Services;
using MOSAIC.Visualization;

namespace MOSAIC.Tests.Components.Basics;

[TestClass]
public class PerformanceContractTests
{
    private sealed class Source() : BaseBlock("source", 100)
    {
        protected override void OnReceive(object sender, object value) { }
    }

    private sealed class Capture : ISubscriber
    {
        public readonly ConcurrentQueue<Matrix<double>> Values = new();
        public void ReceiveInput(object sender, object value) => Values.Enqueue((Matrix<double>)value);
    }

    [DataTestMethod]
    [DataRow(1, 1, SlidingWindow.WindowType.Rectangular)]
    [DataRow(3, 2, SlidingWindow.WindowType.Hamming)]
    [DataRow(19, 7, SlidingWindow.WindowType.Hann)]
    public async Task RingBuffer_PreservesEveryWindowAcrossPacketBoundaries(
        int packetSize, int stride, SlidingWindow.WindowType kind)
    {
        const int size = 7, total = 95, channels = 2;
        await using var window = new SlidingWindow("window", size, stride, kind);
        var capture = new Capture();
        window.AddSubscriber(capture);
        for (int offset = 0; offset < total; offset += packetSize)
        {
            int start = offset;
            var packet = Matrix<double>.Build.Dense(Math.Min(packetSize, total - offset), channels,
                (r, c) => (start + r + 1) * (c + 1));
            window.ReceiveInput(this, packet);
            packet.Clear(); // the buffer must own the rows it retained
        }
        await window.DisposeAsync();
        var output = capture.Values.ToArray();
        Assert.HasCount((total - 1) / stride + 1, output);
        for (int index = 0; index < output.Length; index++)
        {
            if (index > 0) Assert.AreNotSame(output[index - 1], output[index]);
            for (int r = 0; r < size; r++)
            {
                int sample = index * stride - size + 2 + r;
                double weight = kind switch
                {
                    SlidingWindow.WindowType.Hamming => 0.54 - 0.46 * Math.Cos(2 * Math.PI * r / (size - 1)),
                    SlidingWindow.WindowType.Hann => 0.5 * (1 - Math.Cos(2 * Math.PI * r / (size - 1))),
                    _ => 1
                };
                for (int c = 0; c < channels; c++)
                    Assert.AreEqual(Math.Max(0, sample) * (c + 1) * weight, output[index][r, c], 1e-10);
            }
        }
    }

    [TestMethod]
    public async Task RingBuffer_ResizePreservesLatestPendingRowsAndZeroPadding()
    {
        await using var window = new SlidingWindow("resize", 4, 2, SlidingWindow.WindowType.Rectangular);
        var capture = new Capture(); window.AddSubscriber(capture);
        for (int i = 1; i <= 5; i++) window.ReceiveInput(this, Vector<double>.Build.Dense(1, i));
        window.Reconfigure(6, 2, SlidingWindow.WindowType.Rectangular);
        window.ReceiveInput(this, Vector<double>.Build.Dense(1, 6));
        window.ReceiveInput(this, Vector<double>.Build.Dense(1, 7));
        await window.DisposeAsync();
        var output = capture.Values.ToArray();
        CollectionAssert.AreEqual(new double[] { 0, 0, 0, 0, 4, 5 }, output[3].Column(0).ToArray());
        CollectionAssert.AreEqual(new double[] { 0, 0, 4, 5, 6, 7 }, output[4].Column(0).ToArray());
    }

    [TestMethod]
    public void UnchangedInputRate_DoesNotNotifyDownstreamOnEverySample()
    {
        using var source = new Source { SignalRate = 100 };
        using var window = new SlidingWindow("window", 8, 4);
        source.AddSubscriber(window);
        var input = Vector<double>.Build.Dense(2, 1);
        window.ReceiveInput(source, input);
        int changes = 0;
        window.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BaseBlock.DesiredRate)) changes++; };
        for (int i = 0; i < 1000; i++) window.ReceiveInput(source, input);
        Assert.AreEqual(0, changes);
        window.Reconfigure(8, 2, SlidingWindow.WindowType.Hamming);
        Assert.AreEqual(1, changes);
        Assert.AreEqual(50d, window.DesiredRate);
    }

    [TestMethod]
    public async Task RingBuffer_LiveSizeChangesCannotIndexOutsideTheInstalledBuffer()
    {
        using var window = new SlidingWindow("live", 8, 1);
        using var start = new ManualResetEventSlim(false);
        var input = Vector<double>.Build.Dense(2, 1);
        var receives = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 3000; i++) window.ReceiveInput(this, input);
        });
        var edits = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 1000; i++) window.BufferSize = i % 2 == 0 ? 4 : 16;
        });
        start.Set();
        await Task.WhenAll(receives, edits);
        Assert.IsNull(window.LastError);
    }

    [TestMethod]
    public async Task Recording_SnapshotsReusableInputsBeforeBackgroundFormatting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mosaic-snapshots-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var recorder = new CsvDumper(directory, "rows", queueCapacity: 4000);
            var vector = Vector<double>.Build.Dense(1);
            var matrix = Matrix<double>.Build.Dense(1, 1);
            var array = new double[1];
            for (int i = 0; i < 1000; i++)
            {
                vector[0] = matrix[0, 0] = array[0] = i + 0.25;
                recorder.Enqueue(i, vector);
                recorder.EnqueueMatrix(i, matrix);
                recorder.EnqueueLabelled(i, "a,\"b\"", array);
                vector[0] = matrix[0, 0] = array[0] = -1;
            }
            await recorder.FlushAsync();
            await recorder.DisposeAsync();
            var lines = File.ReadAllLines(Path.Combine(directory, "rows.csv"));
            Assert.HasCount(3000, lines);
            Assert.AreEqual(0L, recorder.DroppedRows);
            for (int i = 0; i < 1000; i++)
            {
                string value = (i + 0.25).ToString("G17", CultureInfo.InvariantCulture);
                Assert.AreEqual($"{i},{value}", lines[i * 3]);
                Assert.AreEqual($"{i},0,{value}", lines[i * 3 + 1]);
                Assert.AreEqual($"{i},\"a,\"\"b\"\"\",{value}", lines[i * 3 + 2]);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void Diagnostics_OnlyNotifyWhenValuesChange()
    {
        using var block = new Source();
        var refresh = typeof(BaseBlock).GetMethod("RefreshDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action>(block);
        refresh();
        var names = new List<string?>();
        block.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        for (int i = 0; i < 10; i++) refresh();
        Assert.IsEmpty(names);
        block.ReportError("Invalid input");
        refresh();
        CollectionAssert.AreEquivalent(new[] { "LastError", "HasDiagnostics", "DiagnosticText" }, names);
        names.Clear(); refresh(); Assert.IsEmpty(names);
    }
}

[TestClass, DoNotParallelize]
public class VisualizationSchedulingTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    // Isolate the clock from the singleton used by other blocks. Drive nominal ticks rather
    // than asserting wall-clock deadlines on a loaded test machine without a UI event loop.
    private static VisualizationTimer NewTimer() => (VisualizationTimer)Activator.CreateInstance(typeof(VisualizationTimer), true)!;
    private static Action<object?, EventArgs> Tick(VisualizationTimer timer) => typeof(VisualizationTimer)
        .GetMethod("OnMasterTick", PrivateInstance)!.CreateDelegate<Action<object?, EventArgs>>(timer);

    [TestMethod]
    public void ExistingVisualizationClock_DeliversThirtyAndSixtyFpsGroups()
    {
        using var timer = NewTimer();
        int fast = 0, normal = 0;
        timer.Subscribe(() => fast++, VisualizationTimer.TickRate.Fps60);
        timer.Subscribe(() => normal++, VisualizationTimer.TickRate.Fps30);
        var tick = Tick(timer);
        for (int i = 0; i < 60; i++) tick(null, EventArgs.Empty);
        Assert.AreEqual((60, 30), (fast, normal));
        CollectionAssert.AreEquivalent(new[] { VisualizationTimer.TickRate.Fps30, VisualizationTimer.TickRate.Fps60 },
            Enum.GetValues<VisualizationTimer.TickRate>());
    }

    [TestMethod]
    public void ExistingVisualizationClock_AllowsUnsubscribeInsideCallback()
    {
        using var timer = NewTimer();
        int calls = 0;
        Action callback = () => calls++;
        Action? remove = null;
        remove = () => timer.Unsubscribe(remove!);
        timer.Subscribe(remove, VisualizationTimer.TickRate.Fps60);
        timer.Subscribe(callback, VisualizationTimer.TickRate.Fps60);
        var tick = Tick(timer);
        for (int i = 0; i < 100; i++) tick(null, EventArgs.Empty);
        Assert.AreEqual(1, timer.SubscriberCount(VisualizationTimer.TickRate.Fps60));
        Assert.AreEqual(100, calls);
    }

    [TestMethod]
    public void HiddenVisualization_DerivesTimingWithoutCreatingChartsOrCopyingMatrices()
    {
        bool enabled = BlockVisualization.Enabled;
        BlockVisualization.Enabled = true;
        try
        {
            using var visualization = new BlockVisualization();
            visualization.UpdatePublishRate(25);
            var matrix = Matrix<double>.Build.Dense(8, 2, 1);
            visualization.Feed(matrix);
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) visualization.Feed(matrix);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.AreEqual(0L, allocated);
            Assert.AreEqual(200d, visualization.EffectiveSignalRate);
            foreach (string field in new[] { "_scope", "_spider", "_heatmap" })
                Assert.IsNull(typeof(BlockVisualization).GetField(field, PrivateInstance)!.GetValue(visualization));
            var scope = visualization.Scope!;
            Assert.AreEqual(0.005, scope.SamplePeriodSeconds, 1e-12);
            Assert.AreSame(scope, visualization.Scope);
            Assert.IsNull(typeof(BlockVisualization).GetField("_spider", PrivateInstance)!.GetValue(visualization));
            visualization.Dispose();
            Assert.IsNull(visualization.Scope);
        }
        finally { BlockVisualization.Enabled = enabled; }
    }
}
