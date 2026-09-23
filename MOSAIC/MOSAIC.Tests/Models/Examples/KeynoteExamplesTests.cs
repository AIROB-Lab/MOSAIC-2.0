using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.Analytics;
using MOSAIC.Models.Devices;
using MOSAIC.Models.FlowControl;
using MOSAIC.Models.Streaming;

namespace MOSAIC.Tests.Models.Examples;

/// <summary>
/// Guards the two keynote demo pipelines in Assets/Examples/Keynote: they must build without
/// failures and, driven with synthetic EMG in place of the Myo, reach every output block.
/// </summary>
[TestClass]
[Ignore("Keynote example fixtures are not distributed with the public repository.")]
public class KeynoteExamplesTests
{
    private sealed class Capture : ISubscriber
    {
        public readonly ConcurrentQueue<object?> Values = new();
        public void ReceiveInput(object sender, object value) => Values.Enqueue(value);
        public bool Wait(int count) => SpinWait.SpinUntil(() => Values.Count >= count, 10000);
    }

    private static string ExamplePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Examples", "Keynote", file);

    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    /// <summary>Eight-channel pseudo-EMG: channel-specific amplitude so PCA has structure.</summary>
    private static Vector<double> Emg(int n, double gain) =>
        Vector<double>.Build.Dense(8, c => gain * (0.2 + c * 0.1) * Math.Sin(0.7 * n + c) + 0.05 * Math.Sin(3.1 * n));

    /// <summary>
    /// The test build has no BLE backend, so the Myo cannot be constructed here. Swap it for an
    /// 8-channel sine source of the same shape; the tests inject EMG directly after it anyway.
    /// </summary>
    private static void ReplaceMyoWithHeadlessSource(System.Collections.Generic.Dictionary<string, JsonModel> models) =>
        models["Myo"] = models["Myo"] with { Type = "SinGenerator", Params = new object[] { 8, 1.0, 1.0, 0.0 } };

    private static void DisposeAll(BlockGraphBuilder.BuiltGraph graph)
    {
        foreach (var block in graph.Instances.Values.OfType<IDisposable>()) block.Dispose();
    }

    [TestMethod]
    public void PcaClusters_BuildsAndProjectsCapturedGesturesIntoThreeComponents()
    {
        string json = File.ReadAllText(ExamplePath("01_PcaClusters.json"));
        var models = JsonParser.Parse(json).ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Key });
        ReplaceMyoWithHeadlessSource(models);
        using var services = new ServiceCollection().BuildServiceProvider();
        var graph = BlockGraphBuilder.Build(models, new BlockFactory(services));
        try
        {
            Assert.HasCount(0, graph.Failures, string.Join("; ", graph.Failures.Select(f => f.Reason)));
            var pca = (SupervisedPCA)graph.Instances["PCA"];
            var amplifier = (BaseBlock)graph.Instances["Amplifier"];
            var myo = graph.Instances["Myo"];
            var projections = new Capture();
            pca.AddSubscriber(projections);

            pca.StartCapture("rest");
            for (int n = 0; n < 400; n++) amplifier.ReceiveInput(myo, Emg(n, 0.2));
            Assert.IsTrue(projections.Wait(1), "The Myo -> envelope -> PCA chain must publish.");
            SpinWait.SpinUntil(() => pca.CapturedClusters.TryGetValue("rest", out var pts) && pts.Count > 0, 10000);
            pca.StopCapture();

            pca.StartCapture("power");
            for (int n = 0; n < 400; n++) amplifier.ReceiveInput(myo, Emg(n, 1.0));
            SpinWait.SpinUntil(() => pca.CapturedClusters.TryGetValue("power", out var pts) && pts.Count > 0, 10000);
            pca.StopCapture();

            Assert.IsTrue(projections.Values.All(v => v is Vector<double> x && x.Count == 3), "k = 3 gives the 3D scatter.");
            Assert.HasCount(2, pca.CapturedClusters);
            Assert.IsTrue(pca.CapturedClusters["rest"].Count > 0 && pca.CapturedClusters["power"].Count > 0);
            Assert.AreNotEqual(pca.ClusterColors["rest"], pca.ClusterColors["power"], "Each label gets its own colour.");
        }
        finally { DisposeAll(graph); }
    }

    [TestMethod]
    public void EmgToVrAndHannes_OneControlAlgorithmFeedsBothSinks()
    {
        string json = File.ReadAllText(ExamplePath("02_EmgToVrAndHannes.json"));
        var models = JsonParser.Parse(json).ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Key });
        ReplaceMyoWithHeadlessSource(models);
        int port = FreePort();
        models["UdpToUnity"] = models["UdpToUnity"] with { Params = new object[] { "127.0.0.1", port } };
        using var services = new ServiceCollection().BuildServiceProvider();
        using var unity = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var graph = BlockGraphBuilder.Build(models, new BlockFactory(services));
        try
        {
            Assert.HasCount(0, graph.Failures, string.Join("; ", graph.Failures.Select(f => f.Reason)));
            Assert.HasCount(models.Count, graph.Instances);
            var trigger = (Trigger)graph.Instances["Trigger"];
            var hannes = (HannesHand)graph.Instances["Hannes"];
            var sender = (UdpStreamlinedSender)graph.Instances["UdpToUnity"];
            var amplifier = (BaseBlock)graph.Instances["Amplifier"];
            var myo = graph.Instances["Myo"];
            var handRefs = new Capture();
            var udpVectors = new Capture();
            hannes.AddSubscriber(handRefs);
            sender.AddSubscriber(udpVectors);
            sender.Connect();
            Assert.IsTrue(sender.IsConnected);

            // Live training: hold "rest" quietly, then "power" with strong EMG.
            trigger.SelectAction("rest");
            trigger.StartCapture();
            for (int n = 0; n < 300; n++) amplifier.ReceiveInput(myo, Emg(n, 0.2));
            trigger.StopCapture();
            trigger.SelectAction("power");
            trigger.StartCapture();
            for (int n = 0; n < 300; n++) amplifier.ReceiveInput(myo, Emg(n, 1.0));
            trigger.StopCapture();

            // Free running: predictions must reach the hand and the VR link.
            for (int n = 0; n < 200; n++) amplifier.ReceiveInput(myo, Emg(n, 1.0));
            Assert.IsTrue(handRefs.Wait(1), "The Hannes block must republish the 4-DOF reference.");
            Assert.IsTrue(udpVectors.Wait(1), "The Unity sender must republish the actuation vector.");
            var refs = (Vector<double>)handRefs.Values.Last()!;
            Assert.AreEqual(4, refs.Count, "[hand, wristFE, wristPS, thumb]");
            Assert.IsTrue(refs.All(r => r >= 0 && r <= 1), "Hannes references are normalised 0..1.");
            Assert.AreEqual(11, ((Vector<double>)udpVectors.Values.Last()!).Count, "DirectControl publishes 11 DOAs.");

            unity.Client.ReceiveTimeout = 5000;
            var remote = new IPEndPoint(IPAddress.Any, 0);
            var packet = unity.Receive(ref remote);
            // Frame: count, value-type byte, category, DOA subtype, 8-byte timestamp, payload, counter.
            Assert.AreEqual(1 + 1 + 2 + 8 + 8 + 2, packet.Length, "One double per DOA packet.");
            Assert.AreEqual(1, packet[0], "One value per control packet.");
            Assert.AreEqual(1, packet[2], "Category 1 = control (per-DOA) packet.");
        }
        finally { DisposeAll(graph); }
    }
}
