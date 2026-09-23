using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;
using MOSAIC.Models.Streaming;
using MOSAIC.Tests.Models.SignalProcessing;

namespace MOSAIC.Tests.Models.Streaming;

[TestClass]
public class CrossCorrelationUdpPipelineTests
{
    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private sealed class Capture : ISubscriber, IDisposable
    {
        private readonly AutoResetEvent _ready = new(false);
        public Vector<double>? Last { get; private set; }
        public void ReceiveInput(object sender, object value) { Last = ((Vector<double>)value).Clone(); _ready.Set(); }
        public bool Wait() => _ready.WaitOne(5000);
        public void Dispose() => _ready.Dispose();
    }

    [TestMethod]
    public void UserExampleBuildsAndRunsEndToEndOverRealUdp()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Examples", "BlockTestFiles", "CrossCorrelationUdp.json"));
        int port = FreePort();
        var models = JsonParser.Parse(json).ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Key });
        models["UDPClient_2"] = models["UDPClient_2"] with { Params = new object[] { port, "uint16" } };
        using var services = new ServiceCollection().BuildServiceProvider();
        using var capture = new Capture();
        var graph = BlockGraphBuilder.Build(models, new BlockFactory(services));
        try
        {
            Assert.HasCount(0, graph.Failures, string.Join("; ", graph.Failures.Select(f => f.Reason)));
            Assert.HasCount(5, graph.Instances);
            ((BaseBlock)graph.Instances["Function_1"]).AddSubscriber(capture);
            var input = CrossCorrelationExtendedTests.FourChannels();
            var bytes = new byte[input.RowCount * 4 * 2];
            for (int i = 0; i < input.RowCount * 4; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), (ushort)input[i / 4, i % 4]);
            using var sender = new UdpClient();
            for (int packet = 0; packet < 2; packet++)
            {
                sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
                Assert.IsTrue(capture.Wait(), "The whole UDP → CC → Projector → multiply → power chain must deliver a result.");
                Assert.IsNotNull(capture.Last);
                Assert.AreEqual(1, capture.Last.Count);
                Assert.AreEqual(Math.Pow(50d / 8 * 1.5, 2), capture.Last[0], 1e-10);
            }
        }
        finally
        {
            foreach (var block in graph.Instances.Values.OfType<IDisposable>()) block.Dispose();
        }
    }

    [TestMethod]
    public void UInt16KeepsUnsignedRangeAndRecoversAfterAnOddLengthPacket()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        int port = FreePort();
        using var capture = new Capture();
        using var receiver = UDPClient.ConfigureInput(services, new JsonModel { Type = "UDPClient", Params = [port, "uint16"] });
        receiver.AddSubscriber(capture);
        using var sender = new UdpClient();
        sender.Send(new byte[] { 1, 2, 3 }, 3, new IPEndPoint(IPAddress.Loopback, port));
        var packet = new byte[] { 0, 0, 0xff, 0x7f, 0, 0x80, 0xff, 0xff };
        sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
        Assert.IsTrue(capture.Wait());
        CollectionAssert.AreEqual(new double[] { 0, 32767, 32768, 65535 }, capture.Last!.ToArray());
        Assert.AreEqual("uint16", receiver.ToJsonModel().Params![1]);
        Assert.AreEqual(UDPClient.ParsingFormat.Uint16, receiver.Format);
    }

    [TestMethod]
    public async Task UInt16SendUsesTwoLittleEndianBytesPerValue()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int remotePort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        using var sender = UDPClient.ConfigureInput(services, new JsonModel
        {
            Type = "UDPClient", Params = [FreePort(), "uint16", "127.0.0.1", remotePort]
        });
        sender.ReceiveInput(this, Vector<double>.Build.DenseOfArray([0, 32768, 65535]));
        var received = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0x80, 0xff, 0xff }, received.Buffer);
    }
}
