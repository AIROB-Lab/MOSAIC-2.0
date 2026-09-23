using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Models.FlowControl;
using MOSAIC.Models.Streaming;
using MOSAIC.Services;
using MOSAIC.ViewModels.SignalProcessing;

namespace MOSAIC.Tests.Components.Basics;

[TestClass]
public class PipelineLifetimeTests
{
    [TestMethod]
    public async Task ShutdownWaitsForActiveCallbackAndRejectsQueuedInputs()
    {
        await using var session = new PipelineSession();
        var source = new Probe("source");
        var consumer = new BlockingProbe();
        session.Add(source);
        session.Add(consumer);
        source.AddSubscriber(consumer);
        source.Publish(1d);
        await consumer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        source.Publish(2d);
        var shutdown = session.ClearAsync();
        try
        {
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.AreEqual(0, consumer.Disposals);
        }
        finally { consumer.Release.Set(); }
        await shutdown.WaitAsync(TimeSpan.FromSeconds(3));
        source.Publish(3d);
        consumer.ReceiveInput(this, 4d);
        Assert.AreEqual(1, consumer.Receives);
        Assert.AreEqual(1, consumer.Disposals);
        Assert.AreEqual(1, source.Disposals);
        Assert.IsEmpty(session.Blocks);
    }

    [TestMethod]
    public async Task DeleteDisconnectsConsumerAndKeepsOtherBranchRunning()
    {
        await using var session = new PipelineSession();
        var source = new Probe("source");
        var deleted = new Probe("deleted");
        var survivor = new Probe("survivor");
        foreach (var block in new[] { source, deleted, survivor }) session.Add(block);
        source.AddSubscriber(deleted);
        source.AddSubscriber(survivor);
        await session.RemoveAsync([deleted]);
        source.Publish(1d);
        await survivor.FirstReceive.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, deleted.Receives);
        Assert.AreEqual(1, deleted.Disposals);
        Assert.AreEqual(1, survivor.Receives);
        Assert.HasCount(2, session.Blocks);
    }

    [TestMethod]
    public async Task ReloadReleasesUdpPortBeforeReplacementConstruction()
    {
        int port;
        using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        await using var session = new PipelineSession();
        var factory = new UdpFactory(port);
        var models = new Dictionary<string, JsonModel> { ["receiver"] = new() { Name = "receiver", Type = "UDPClient" } };
        for (int reload = 0; reload < 3; reload++)
        {
            var graph = await session.ReplaceAsync(models, factory);
            Assert.IsEmpty(graph.Failures);
            Assert.HasCount(1, session.Blocks);
        }
        await session.ClearAsync();
        using var rebound = new UdpClient(new IPEndPoint(IPAddress.Any, port));
    }

    [TestMethod]
    public async Task DisposalClosesRecordingAndIsAwaitableMoreThanOnce()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mosaic-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var block = new Probe("recorded");
            block.StartRecording(folder);
            for (int i = 0; i < 100; i++) block.Publish((double)i);
            await block.DisposeAsync();
            await block.DisposeAsync();
            var file = Directory.GetFiles(folder, "*.csv").Single();
            var lines = File.ReadAllLines(file);
            CollectionAssert.AreEqual(Enumerable.Range(0, 100).Select(i => (double)i).ToArray(),
                lines.Select(line => double.Parse(line.Split(',')[1], CultureInfo.InvariantCulture)).ToArray(),
                "All accepted rows must be written before disposal returns.");
            using var exclusive = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.AreEqual(1, block.Disposals);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public async Task DispatcherDisposalWaitsForRemovedSubscriptions()
    {
        var dispatcher = new OutputDispatcher();
        var consumer = new BlockingProbe();
        dispatcher.AddSubscriber(consumer);
        dispatcher.Dispatch(this, 1d);
        await consumer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        dispatcher.RemoveSubscriber(consumer);
        var shutdown = dispatcher.DisposeAsync().AsTask();
        try { Assert.IsFalse(shutdown.IsCompleted); }
        finally { consumer.Release.Set(); }
        await shutdown.WaitAsync(TimeSpan.FromSeconds(3));
        await consumer.DisposeAsync();
    }

    [TestMethod]
    public async Task AsyncDisposalAlsoWaitsForCleanupStartedBySynchronousDisposal()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = new Probe("cleanup");
        block.TrackBackgroundCleanup(finished.Task);
        block.Dispose();
        var disposal = block.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);
        finished.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, block.Disposals);
    }

    [TestMethod]
    public async Task ShutdownStopsClockThreadAndPreventsRestart()
    {
        await using var session = new PipelineSession();
        var clock = new ClockBlock();
        session.Add(clock);
        clock.Start();
        Assert.IsTrue(clock.Running);
        await session.ClearAsync();
        Assert.IsFalse(clock.Running);
        Assert.Throws<ObjectDisposedException>(() => clock.Start());
    }

    [TestMethod]
    public async Task BlockReusesAndDisposesItsCardViewModel()
    {
        var block = new Function("function", 100, value => value);
        var first = block.GetOrCreateOwned(() => new FunctionViewModel(block));
        var second = block.GetOrCreateOwned(() => new FunctionViewModel(block));
        Assert.AreSame(first, second);
        block.FunctionType = "clip";
        Assert.IsTrue(first.ShowParam2);
        await block.DisposeAsync();
        Assert.IsNull(block.Viz);
        block.FunctionType = "abs";
        Assert.IsTrue(first.ShowParam2, "Disposed ViewModel must no longer observe model changes.");
    }

    [TestMethod]
    public async Task OneFailingDeviceDoesNotPreventOtherBlocksFromDisposing()
    {
        var session = new PipelineSession();
        var failing = new FailingProbe();
        var survivor = new Probe("survivor");
        session.Add(failing);
        session.Add(survivor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ClearAsync());
        Assert.AreEqual(1, survivor.Disposals);
        Assert.IsEmpty(session.Blocks);
        failing.ReceiveInput(this, 1d);
        Assert.AreEqual(0, failing.Receives);
    }

    private class Probe(string name) : BaseBlock(name)
    {
        public int Receives;
        public int Disposals;
        public TaskCompletionSource FirstReceive { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void OnReceive(object sender, object value)
        {
            Interlocked.Increment(ref Receives);
            FirstReceive.TrySetResult();
        }
        public override void Dispose() { Disposals++; base.Dispose(); }
        public void StartRecording(string folder) => InitDumper(new EmptyServices(), folder);
        public void TrackBackgroundCleanup(Task cleanup) => TrackCleanup(cleanup);
    }

    private sealed class BlockingProbe() : Probe("blocking")
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        protected override void OnReceive(object sender, object value)
        {
            base.OnReceive(sender, value);
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test did not release callback.");
        }
    }

    private sealed class FailingProbe() : Probe("failing")
    {
        public override void Dispose() => throw new InvalidOperationException("Test device release failure.");
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class UdpFactory(int port) : IBlockFactory
    {
        public object Create(JsonModel model) => new UDPClient(model.Name!, 0, port, UDPClient.ParsingFormat.Double);
        public T Create<T>(JsonModel model) where T : class => (T)Create(model);
    }
}
