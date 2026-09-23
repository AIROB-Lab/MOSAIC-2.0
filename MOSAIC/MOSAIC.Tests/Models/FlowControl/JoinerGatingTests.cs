using System;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using Joiner = global::MOSAIC.Models.Joiner;

namespace MOSAIC.Tests.Models.FlowControl;

/// <summary>
/// Covers what the Joiner waits for before it starts publishing.
/// </summary>
/// <remarks>
/// <para>
/// The block withholds output until every declared input has delivered once, so that slot <c>k</c>
/// of the joined vector belongs to declared input <c>k</c> for the whole run. Without it a source
/// that had not spoken yet contributed no group at all, the vector came out short, and every source
/// after the gap silently landed in someone else's slot — a BodyRig bound its torso to an upper-arm
/// IMU and reported itself healthy.
/// </para>
/// <para>
/// The wait has to be over inputs that can actually deliver, though. <c>OnReceive</c> drops any
/// payload that is neither a Vector nor a Matrix before recording it, so an input that only ever
/// publishes something else — a Clock, which sends a bare timestamp — could never fill its slot and
/// the gate stayed shut for the life of the process. Four shipped configs list a clock that way
/// (<c>test3</c>, <c>test4</c>, <c>slopesignchange</c>, <c>zerocrossings</c>) and published nothing
/// at all as a result.
/// </para>
/// </remarks>
[TestClass]
public class JoinerGatingTests
{
    /// <summary>A clock among the declared inputs must not hold the gate shut.</summary>
    [TestMethod]
    public void Joiner_WithAClockAmongItsInputs_StillPublishes()
    {
        var (graph, joiner) = Build("""
        {
          "timer": { "Type": "Clock", "DesiredRate": 200 },
          "sin1":  { "Type": "SinGenerator", "Inputs": ["timer"], "Params": [4, 1.0, 0.2, 0.0] },
          "join":  { "Type": "Joiner", "Inputs": ["sin1", "timer"], "Params": ["timer:sin1"] }
        }
        """);
        try
        {
            var timer = (BaseBlock)graph.Instances["timer"];
            var sin1  = (BaseBlock)graph.Instances["sin1"];

            var capture = new Capture();
            joiner.AddSubscriber(capture);

            // Exactly what each source really sends: the clock a timestamp, the generator a vector.
            joiner.ReceiveInput(timer, 1.0);
            joiner.ReceiveInput(sin1, Vector<double>.Build.Dense(4, 1.0));

            Assert.IsTrue(capture.Wait(2000),
                "a clock input can never deliver a vector, so waiting on it means never publishing");
            CollectionAssert.DoesNotContain(joiner.PendingInputs.ToArray(), "timer",
                "the clock should not be counted as an outstanding input");
        }
        finally { Dispose(graph); }
    }

    /// <summary>
    /// The guarantee the gate exists for: a real data source that has not spoken still blocks.
    /// </summary>
    [TestMethod]
    public void Joiner_StillWaitsForADataInputThatHasNotDelivered()
    {
        var (graph, joiner) = Build(TwoGenerators);
        try
        {
            var sin1 = (BaseBlock)graph.Instances["sin1"];

            var capture = new Capture();
            joiner.AddSubscriber(capture);

            // Only the pacer speaks. sin2 has never delivered, so publishing now would hand
            // downstream a vector that is short by sin2's channels.
            joiner.ReceiveInput(sin1, Vector<double>.Build.Dense(4, 1.0));

            Assert.IsFalse(capture.Wait(500), "output must be withheld until every data input is in");
            CollectionAssert.Contains(joiner.PendingInputs.ToArray(), "sin2");
        }
        finally { Dispose(graph); }
    }

    /// <summary>And it releases as soon as the missing source does deliver, in declared order.</summary>
    [TestMethod]
    public void Joiner_PublishesOnceEveryDataInputHasDelivered()
    {
        var (graph, joiner) = Build(TwoGenerators);
        try
        {
            var sin1 = (BaseBlock)graph.Instances["sin1"];
            var sin2 = (BaseBlock)graph.Instances["sin2"];

            var capture = new Capture();
            joiner.AddSubscriber(capture);

            joiner.ReceiveInput(sin2, Vector<double>.Build.Dense(4, 2.0));  // non-pacer, banked
            joiner.ReceiveInput(sin1, Vector<double>.Build.Dense(4, 1.0));  // pacer, triggers publish

            Assert.IsTrue(capture.Wait(2000), "both inputs are in, so it should publish");
            Assert.AreEqual(0, joiner.PendingInputs.Count);

            var joined = (Vector<double>)capture.Captured!;
            Assert.AreEqual(8, joined.Count, "four channels from each input");
            Assert.AreEqual(1.0, joined[0], 1e-9, "sin1 is declared first, so it owns slots 0-3");
            Assert.AreEqual(2.0, joined[4], 1e-9, "sin2 owns slots 4-7");
        }
        finally { Dispose(graph); }
    }

    // ── fixtures ────────────────────────────────────────────────────────────

    private const string TwoGenerators = """
    {
      "timer": { "Type": "Clock", "DesiredRate": 200 },
      "sin1":  { "Type": "SinGenerator", "Inputs": ["timer"], "Params": [4, 1.0, 0.2, 0.0] },
      "sin2":  { "Type": "SinGenerator", "Inputs": ["timer"], "Params": [4, 1.0, 0.5, 0.0] },
      "join":  { "Type": "Joiner", "Inputs": ["sin1", "sin2"], "Params": ["timer:sin1"] }
    }
    """;

    private static (BlockGraphBuilder.BuiltGraph graph, Joiner joiner) Build(string json)
    {
        var models = JsonParser.Parse(json).ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Key });
        var sp = new ServiceCollection().BuildServiceProvider();
        var graph = BlockGraphBuilder.Build(models, new BlockFactory(sp));
        return (graph, (Joiner)graph.Instances["join"]);
    }

    private static void Dispose(BlockGraphBuilder.BuiltGraph graph)
    {
        foreach (var i in graph.Instances.Values) (i as IDisposable)?.Dispose();
    }

    private sealed class Capture : MOSAIC.Components.Interfaces.ISubscriber
    {
        private readonly System.Threading.ManualResetEventSlim _got = new(false);
        public object? Captured { get; private set; }

        public void ReceiveInput(object sender, object value)
        {
            Captured = value;
            _got.Set();
        }

        public bool Wait(int ms) => _got.Wait(ms);
    }
}
