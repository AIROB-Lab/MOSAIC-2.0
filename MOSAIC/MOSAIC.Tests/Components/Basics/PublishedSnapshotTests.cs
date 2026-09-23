using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Interfaces;
using MOSAIC.Components.Manager.ControlAlgorithm;
using MOSAIC.Models.FlowControl;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Models.Streaming;

namespace MOSAIC.Tests.Components.Basics;

[TestClass]
public class PublishedSnapshotTests
{
    [TestMethod]
    public async Task ResamplerRetainedOutputsSurviveLaterPublicationsAndInputMutation()
    {
        await using var block = new Resampler("resampler", 100, 100);
        var capture = new Capture<Vector<double>>();
        block.AddSubscriber(capture);
        var retained = new List<Vector<double>>();
        var input = DenseVector.Create(2, 0);
        for (int value = 1; value <= 6; value++)
        {
            input[0] = value;
            input[1] = -value;
            block.ReceiveInput(this, input);
            retained.Add(await capture.Next());
        }
        input.Clear();
        for (int i = 0; i < retained.Count; i++)
            CollectionAssert.AreEqual(new[] { (double)i + 1, -(double)i - 1 }, retained[i].ToArray());
        Assert.AreNotSame(retained[0], retained[2]);
    }

    [TestMethod]
    public async Task BlenderCommandsDoNotChangeAfterAnotherInput()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        await using var block = new BlenderArm(port: port);
        block.Connect();
        var capture = new Capture<Vector<double>>();
        block.AddSubscriber(capture);
        block.ReceiveInput(this, new Dictionary<DegreesOfActuation, double> { [DegreesOfActuation.Index] = 25 });
        var first = await capture.Next();
        block.ReceiveInput(this, new Dictionary<DegreesOfActuation, double> { [DegreesOfActuation.Index] = 75 });
        var second = await capture.Next();
        Assert.AreEqual(0.25, first[2]);
        Assert.AreEqual(0.75, second[2]);
        Assert.AreNotSame(first, second);
    }

    [TestMethod]
    public async Task ControlDictionarySurvivesStrategyUpdatesAndReset()
    {
        await using var block = new ControlAlgorithm("control", 100, new TestStrategy());
        var capture = new Capture<Dictionary<DegreesOfActuation, double>>();
        block.AddSubscriber(capture);
        block.ReceiveInput(this, DenseVector.OfArray([25d]));
        var first = await capture.Next();
        block.ReceiveInput(this, DenseVector.OfArray([75d]));
        var second = await capture.Next();
        block.ResetStrategy();
        Assert.AreEqual(25d, first[DegreesOfActuation.Index]);
        Assert.AreEqual(75d, second[DegreesOfActuation.Index]);
        Assert.AreNotSame(first, second);
    }

    private sealed class TestStrategy : ControlStrategyBase
    {
        public override string Name => "test";
        protected override void InitializeDefaults() => ControlDict[DegreesOfActuation.Index] = 0;
        public override void ProcessPrediction(Vector<double> prediction) => ControlDict[DegreesOfActuation.Index] = prediction[0];
    }

    private sealed class Capture<T> : ISubscriber
    {
        private readonly Channel<T> _values = Channel.CreateUnbounded<T>();
        public void ReceiveInput(object sender, object value) => _values.Writer.TryWrite((T)value);
        public Task<T> Next() => _values.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }
}
