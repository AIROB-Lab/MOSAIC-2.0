using System;
using System.IO;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Models.Devices;
using MOSAIC.Models.Streaming;
using MOSAIC.Tests.TestSupport;
using ScopeMonitorControl = MOSAIC.Visualization.ScopeMonitor.ScopeMonitor;

namespace MOSAIC.Tests.Visualization;

/// <summary>
/// Guards the scope's time base against blocks that never declare their sample rate.
/// </summary>
/// <remarks>
/// <para>
/// The scope plots through a ScottPlot <c>DataStreamer</c>, whose <c>Period</c> is the time the
/// x-axis advances for <b>each row written</b>. Batched blocks write every row of every matrix —
/// <c>EnqueueBatch</c> is deliberately exempt from the ingest throttle — so the period has to be
/// one over the <b>sample</b> rate, never one over the rate at which packets are published. For a
/// source ticking at <c>T</c> Hz and emitting <c>N</c> rows per tick, that is <c>N × T</c>.
/// </para>
/// <para>
/// Nothing wires that up automatically: <c>SignalRate</c> travels along the block graph for
/// downstream DSP, but the local visualization has no route to it and only learns the rate if the
/// block calls <c>Viz.UpdateSignalRate</c>. A block that forgets leaves the scope on a fallback
/// derived from its own window and buffer size — a display constant of 200 Hz at the defaults —
/// and the trace then scrolls <c>trueRate / 200</c> times too fast with no error anywhere.
/// </para>
/// <para>
/// These tests ensure every scope-capable block explicitly propagates its signal rate, preventing
/// plausible-looking plots with an incorrect time axis.
/// </para>
/// <para>
/// They read the declaration rather than the rendered plot, so they need no window and are
/// unaffected by <c>BlockVisualization.Enabled = false</c> in <see cref="TestBootstrap"/>.
/// </para>
/// </remarks>
[TestClass]
public class ScopeTimebaseTests
{
    // ── The invariant itself ────────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(100.0,  0.01)]      //  100 Hz → 10   ms per row
    [DataRow(200.0,  0.005)]     //  200 Hz →  5   ms
    [DataRow(1000.0, 0.001)]     // 1000 Hz →  1   ms  (MccDaq at 100 Hz × 10 scans)
    [DataRow(2000.0, 0.0005)]    // 2000 Hz →  0.5 ms
    [DataRow(4000.0, 0.00025)]   // 4000 Hz →  0.25 ms — past the 10000-point cap, so the
                                 // buffer-derived fallback (1 ms) and the correct period differ
    public void SamplePeriod_IsTheInverseOfTheDeclaredSignalRate(double rate, double expectedPeriod)
    {
        using var scope = new ScopeMonitorControl();

        scope.UpdateSignalRate(rate);

        Assert.AreEqual(expectedPeriod, scope.SamplePeriodSeconds, expectedPeriod * 1e-9,
            $"a scope told {rate} Hz must advance its x-axis by 1/{rate} s per row");
    }

    /// <summary>
    /// Pins the fallback, so it is visible what an undeclared rate actually costs.
    /// </summary>
    /// <remarks>
    /// At the defaults — a 10 s window over 2000 points — the fallback is 5 ms, i.e. the scope
    /// behaves as though every source ran at 200 Hz. A 2 kHz source is then drawn 10× too fast.
    /// Note that a genuinely 200 Hz source looks perfect either way, which is why the shipped
    /// 200 Hz demos never revealed this.
    /// </remarks>
    [TestMethod]
    public void SamplePeriod_WithNoDeclaredRate_FallsBackToADisplayConstant()
    {
        using var scope = new ScopeMonitorControl();

        Assert.AreEqual(0.0, scope.SignalRate,
            "a fresh scope should not claim to know a signal rate");
        Assert.AreEqual(0.005, scope.SamplePeriodSeconds, 1e-9,
            "the undeclared fallback is window/points = 10 s / 2000 = 5 ms, i.e. an assumed 200 Hz");
    }

    // ── Producers must declare ──────────────────────────────────────────────

    [TestMethod]
    public void SinGenerator_OneSamplePerTick_DeclaresTheTickRate()
    {
        using var graph = new Graph(Pipeline(scansPerPacket: 1));

        var gen = graph.Block<SinGenerator>("gen");

        Assert.AreEqual(100.0, gen.SampleRate, 1e-9);
        Assert.AreEqual(100.0, gen.Viz.SignalRate, 1e-9,
            "one row per 100 Hz tick is a 100 Hz signal");
    }

    /// <summary>
    /// The regression test for the packet case: ten rows per 100 Hz tick is a 1000 Hz signal, and
    /// declaring the 100 Hz tick rate instead would draw it ten times too fast.
    /// </summary>
    [TestMethod]
    public void SinGenerator_Packetised_DeclaresTheSampleRateNotTheTickRate()
    {
        using var graph = new Graph(Pipeline(scansPerPacket: 10));

        var gen = graph.Block<SinGenerator>("gen");

        Assert.AreEqual(1000.0, gen.SampleRate, 1e-9);
        Assert.AreEqual(1000.0, gen.Viz.SignalRate, 1e-9,
            "ten rows per 100 Hz tick is a 1000 Hz signal, not a 100 Hz one");
    }

    /// <summary>
    /// The same regression one hop downstream, and the harder half of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A windowing block appends every row of an incoming matrix and shows the pre-window rows on
    /// its own scope, so that scope runs at the source's <b>sample</b> rate. But the rate it
    /// inherits through <c>InputRate</c> is the source's <b>publish</b> rate, which behind a
    /// batching source is smaller by exactly the packet size — taking it would draw the trace ten
    /// times too fast here.
    /// </para>
    /// <para>
    /// The two rates also arrive at different times: <c>SignalRate</c> propagates when the graph is
    /// wired, while <c>InputRate</c> is only set by <c>TryInheritRate</c> on the first value that
    /// actually arrives. So the declaration is checked both before and after data flows — reacting
    /// to only one of the two events would leave whichever rate happened to land first in place.
    /// </para>
    /// </remarks>
    [TestMethod]
    public void SlidingWindow_BehindAPacketisedSource_DeclaresTheSampleRateNotThePublishRate()
    {
        using var graph = new Graph(Pipeline(scansPerPacket: 10, withWindow: true));

        var gen    = graph.Block<SinGenerator>("gen");
        var window = graph.Block<MOSAIC.Models.SignalProcessing.SlidingWindow>("window");

        Assert.AreEqual(100.0, gen.DesiredRate, 1e-9,
            "precondition: the source publishes one packet per 100 Hz tick");

        Assert.AreEqual(1000.0, window.Viz!.SignalRate, 1e-9,
            "on wiring: the rows inside those packets are 1000 Hz, and this scope plots rows");

        // Now let a real packet through, which is what sets InputRate to the 100 Hz publish rate.
        window.ReceiveInput(gen, BlockHarness.CaptureMatrix(gen, 0.0));

        Assert.AreEqual(100.0, window.InputRate, 1e-9,
            "precondition: InputRate is the publish rate, and only lands once data has flowed");
        Assert.AreEqual(1000.0, window.Viz.SignalRate, 1e-9,
            "after data: the publish rate must not have displaced the sample rate");
    }

    // ── Packet shape and continuity ─────────────────────────────────────────

    [TestMethod]
    public void SinGenerator_Packetised_PublishesOneRowPerScan()
    {
        using var graph = new Graph(Pipeline(scansPerPacket: 10));

        var gen = graph.Block<SinGenerator>("gen");
        var packet = BlockHarness.CaptureMatrix(gen, 0.0);

        Assert.AreEqual(10, packet.RowCount, "rows = ScansPerPacket");
        Assert.AreEqual(4, packet.ColumnCount, "columns = channels");
    }

    /// <summary>
    /// Rows must be spaced one sample period apart and the phase must carry across tick
    /// boundaries, so that a discontinuity seen on the scope can only have come from the plotting
    /// path rather than from the generator feeding it.
    /// </summary>
    [TestMethod]
    public void SinGenerator_Packetised_PhaseRunsContinuouslyAcrossPackets()
    {
        using var graph = new Graph(Pipeline(scansPerPacket: 10));

        var gen = graph.Block<SinGenerator>("gen");

        var first  = BlockHarness.CaptureMatrix(gen, 0.0);
        var second = BlockHarness.CaptureMatrix(gen, 0.0);

        // 1 Hz sine sampled at 1000 Hz: sample n on channel 0 is sin(2π·n/1000).
        // The two packets together are samples 0..19, so every one of them must sit on that curve.
        var rows = Enumerable.Range(0, 10).Select(r => first[r, 0])
            .Concat(Enumerable.Range(0, 10).Select(r => second[r, 0]))
            .ToArray();

        for (int n = 0; n < rows.Length; n++)
        {
            double expected = Math.Sin(2 * Math.PI * n / 1000.0);
            Assert.AreEqual(expected, rows[n], 1e-9,
                $"sample {n} is off the curve — the phase stepped at a packet boundary");
        }
    }

    // ── The two device blocks this change exists for ────────────────────────

    /// <summary>
    /// The board settles on its own scan rate, so the declaration hangs off that property rather
    /// than off <c>Connect</c> — which also means it can be checked without a board attached.
    /// </summary>
    [TestMethod]
    public void MccDaqBoard_DeclaresTheSettledScanRateAsItsSampleRate()
    {
        using var board = new MccDaqBoard("Daq") { ScansPerPacket = 10 };

        Assert.AreEqual(0.0, board.Viz.SignalRate, "precondition: nothing declared before a scan starts");

        // What StartScan assigns once the driver reports back: 100 Hz clock × 10 scans per packet.
        board.ActualScanRate = 1000;

        Assert.AreEqual(1000.0, board.SignalRate, 1e-9, "the graph must carry the scan rate");
        Assert.AreEqual(1000.0, board.Viz.SignalRate, 1e-9, "and so must the scope");
    }

    /// <summary>
    /// A packet producer must not set a desired rate on its visualization.
    /// </summary>
    /// <remarks>
    /// <c>BlockVisualization.Feed(Matrix)</c> trims a batch to its last <c>stride</c> rows when
    /// <c>_signalRate &gt; _desiredRate &gt; 0</c> and <c>rows &gt; stride</c>. That exists for
    /// overlapping <c>SlidingWindow</c> output, where re-plotting the overlap draws the same samples
    /// twice. For a device emitting contiguous packets there is no overlap, so trimming would
    /// silently discard rows that ought to be plotted — and now that these blocks declare a signal
    /// rate, the only thing still keeping the trim dormant is the absent desired rate.
    /// </remarks>
    [TestMethod]
    public void PacketProducers_DoNotArmTheOverlapTrim()
    {
        using var board = new MccDaqBoard("Daq") { ScansPerPacket = 10 };
        board.ActualScanRate = 1000;

        using var graph = new Graph(Pipeline(scansPerPacket: 10));
        var gen = graph.Block<SinGenerator>("gen");

        foreach (var (name, viz) in new[]
                 {
                     ("MccDaq", board.Viz), ("SinGenerator", gen.Viz),
                 })
        {
            Assert.IsTrue(viz.SignalRate > 0, $"{name} should declare a signal rate");
            Assert.AreEqual(0.0, viz.DesiredRate,
                $"{name} must leave the desired rate unset, or Feed(Matrix) starts discarding rows");
        }
    }

    // ── The two halves joined ───────────────────────────────────────────────

    /// <summary>
    /// Every other producer test reads the rate a block <em>declared</em>. This one carries that
    /// value the last hop into a real scope and checks the period that comes out, so the two halves
    /// of the contract cannot drift apart while both still pass.
    /// </summary>
    [TestMethod]
    public void ADeclaredRate_BecomesTheScopePeriod()
    {
        using var graph = new Graph(Pipeline(scansPerPacket: 10));
        var gen = graph.Block<SinGenerator>("gen");

        using var scope = new ScopeMonitorControl();
        scope.UpdateSignalRate(gen.Viz.SignalRate);

        Assert.AreEqual(0.001, scope.SamplePeriodSeconds, 1e-12,
            "1000 Hz declared by the block must arrive as a 1 ms period on the plot");
    }

    /// <summary>
    /// A rate that changes after the graph is running must still reach the scope.
    /// </summary>
    /// <remarks>
    /// This is the case a naive fix gets wrong. <c>BaseBlock</c>'s propagation refuses to overwrite
    /// a signal rate a block already holds, and <c>SlidingWindow</c> writes its own — so reading
    /// only <c>SignalRate</c> would pin the scope to whatever value was inferred first and quietly
    /// ignore the clock being changed from the card.
    /// </remarks>
    [TestMethod]
    public void SlidingWindow_WhenTheClockChanges_FollowsTheNewRate()
    {
        using var graph = new Graph(Pipeline(scansPerPacket: 1, withWindow: true));

        var clock  = graph.Block<MOSAIC.Models.FlowControl.ClockBlock>("clock");
        var gen    = graph.Block<SinGenerator>("gen");
        var window = graph.Block<MOSAIC.Models.SignalProcessing.SlidingWindow>("window");

        window.ReceiveInput(gen, BlockHarness.CaptureVector(gen, 0.0));
        Assert.AreEqual(100.0, window.Viz!.SignalRate, 1e-9, "precondition: settled at the 100 Hz clock");

        clock.DesiredRate = 400;
        window.ReceiveInput(gen, BlockHarness.CaptureVector(gen, 0.0));

        Assert.AreEqual(400.0, gen.Viz.SignalRate, 1e-9, "the source followed the clock");
        Assert.AreEqual(400.0, window.Viz.SignalRate, 1e-9,
            "and so did the window — a latched 100 Hz here would draw its trace 4x too fast");
    }

    // ── Rates derived automatically, for blocks that declare none ───────────

    /// <summary>
    /// The reported case: a SlopeSignChanges behind a stride-25 window, reading 8 Hz on its card
    /// and taking 25 seconds of wall clock to draw one second of trace.
    /// </summary>
    /// <remarks>
    /// A 200 Hz clock through a window of stride 25 publishes at 200/25 = 8 Hz, and SSC emits one
    /// row per publish — so its scope must run at 8 Hz, a 125 ms period. It declares no rate of its
    /// own (30 of the 37 blocks that plot do not), so before the derivation it sat on the fallback
    /// 5 ms period: 8 rows a second advanced the axis 40 ms per real second, hence the 25x crawl.
    /// </remarks>
    [TestMethod]
    public void SlopeSignChanges_BehindAStrideWindow_DerivesItsPublishRate()
    {
        using var graph = new Graph("""
        {
          "timer":  { "Type": "Clock", "DesiredRate": 200 },
          "sin1":   { "Type": "SinGenerator", "Inputs": ["timer"], "Params": [4, 1.0, 2.0, 0.0] },
          "window": { "Type": "SlidingWindow", "Inputs": ["sin1"], "Params": [100, 25, "Rectangular"],
                      "DesiredRate": 200 },
          "ssc":    { "Type": "SlopeSignChanges", "Inputs": ["window"], "Params": [0.01] }
        }
        """);

        var window = graph.Block<MOSAIC.Models.SignalProcessing.SlidingWindow>("window");
        var ssc    = graph.Block<MOSAIC.Models.FlowControl.SlopeSignChanges>("ssc");

        Assert.AreEqual(8.0, ssc.DesiredRate, 1e-9,
            "precondition: 200 Hz through stride 25 publishes at 8 Hz — the rate shown on the card");

        // One window through SSC is all it takes for the shape of a feed to be known.
        ssc.ReceiveInput(window, Matrix<double>.Build.Dense(100, 4, (r, c) => Math.Sin(r * 0.3 + c)));

        Assert.AreEqual(8.0, ssc.Viz!.EffectiveSignalRate, 1e-9,
            "one row per 8 Hz publish is an 8 Hz signal");

        using var scope = new ScopeMonitorControl();
        scope.UpdateSignalRate(ssc.Viz.EffectiveSignalRate);
        Assert.AreEqual(0.125, scope.SamplePeriodSeconds, 1e-12,
            "8 Hz is a 125 ms period — one second of trace per second of wall clock");
    }

    /// <summary>A block emitting N rows per publish derives N times its publish rate.</summary>
    [TestMethod]
    public void MatrixFeed_DerivesRowsTimesThePublishRate()
    {
        using var graph = new Graph(Pipeline(scansPerPacket: 10));
        var gen = graph.Block<SinGenerator>("gen");

        // SinGenerator declares its rate outright, so read the derivation off a bare visualization
        // driven the same way BaseBlock drives one.
        using var viz = new MOSAIC.Visualization.BlockVisualization();
        viz.UpdatePublishRate(100);
        viz.Feed(Matrix<double>.Build.Dense(10, 4));

        Assert.AreEqual(1000.0, viz.DerivedSignalRate, 1e-9,
            "ten rows per 100 Hz publish is a 1000 Hz signal");
        Assert.AreEqual(1000.0, gen.Viz.EffectiveSignalRate, 1e-9,
            "and the block that declares the same thing explicitly agrees");
    }

    /// <summary>
    /// An explicit declaration always wins — the derivation must not overwrite it.
    /// </summary>
    /// <remarks>
    /// Packet batches can vary in size with transport coalescing, so publishRate x rows may be
    /// meaningless and an explicitly declared ADC rate must remain authoritative.
    /// </remarks>
    [TestMethod]
    public void AnExplicitDeclaration_IsNotOverwrittenByTheDerivation()
    {
        using var viz = new MOSAIC.Visualization.BlockVisualization();

        viz.UpdateSignalRate(2000);
        viz.UpdatePublishRate(44);
        viz.Feed(Matrix<double>.Build.Dense(18, 4));   // a coalesced batch: 44 x 18 would be wrong

        Assert.AreEqual(2000.0, viz.EffectiveSignalRate, 1e-9,
            "the declared ADC rate must survive");
        Assert.AreEqual(0.0, viz.DerivedSignalRate, 1e-9,
            "and the derivation should not even have run");
    }

    // ── The shipped demo ────────────────────────────────────────────────────

    /// <summary>
    /// The example pipeline must actually load.
    /// </summary>
    /// <remarks>
    /// Nothing else in the suite opens a file from <c>Assets/Examples</c>, so a config can ship
    /// broken and stay green — which is exactly what happened to this one: its
    /// <c>SlidingWindow</c> was written with two params, and <c>ConfigureInput</c> reads a third
    /// unguarded, so <c>BlockGraphBuilder.Build</c> threw and none of the blocks the demo exists to
    /// compare were ever created. Building it here is a cheap guard against the whole class of
    /// param-count drift.
    /// </remarks>
    [TestMethod]
    public void ShippedTimebaseExample_LoadsAndWiresItsBlocks()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "Assets", "Examples", "BlockTestFiles", "ScopeTimebaseTest.json");

        Assert.IsTrue(File.Exists(path), $"the example was not deployed beside the tests: {path}");

        using var graph = new Graph(File.ReadAllText(path));

        var perSample  = graph.Block<SinGenerator>("perSample");
        var packetised = graph.Block<SinGenerator>("packetised");
        graph.Block<MOSAIC.Models.SignalProcessing.SlidingWindow>("windowed");

        // The demo's whole point: the same 1 Hz sine down two delivery paths, and both scopes must
        // therefore show the same number of cycles across the window.
        Assert.AreEqual(100.0,  perSample.Viz.SignalRate,  1e-9, "one row per 100 Hz tick");
        Assert.AreEqual(1000.0, packetised.Viz.SignalRate, 1e-9, "ten rows per 100 Hz tick");
        Assert.AreEqual(perSample.Frequency, packetised.Frequency, 1e-9,
            "the two generators must produce the same frequency for the comparison to mean anything");
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    [TestMethod]
    public void ForcedPublicationRateChange_RefreshesOwnedAndDownstreamVisualizations()
    {
        using var source = new RateProbe("source");
        using var downstream = new RateProbe("downstream");
        using var declared = new RateProbe("declared");
        source.DesiredRate = 4;
        source.AddSubscriber(downstream);
        downstream.AddSubscriber(declared);
        source.Viz.Feed(Vector<double>.Build.Dense(1));
        downstream.Viz.Feed(Matrix<double>.Build.Dense(3, 1));
        declared.Viz.UpdateSignalRate(2000);
        declared.Viz.Feed(Vector<double>.Build.Dense(1));

        source.ChangeRate(8);

        // Check immediately: another ReceiveInput must not be needed to repair a stale rate.
        Assert.AreEqual(8d, source.Viz.EffectiveSignalRate);
        Assert.AreEqual(8d, downstream.DesiredRate);
        Assert.AreEqual(24d, downstream.Viz.EffectiveSignalRate);
        Assert.AreEqual(8d, declared.Viz.PublishRate);
        Assert.AreEqual(2000d, declared.Viz.EffectiveSignalRate);
        Assert.AreEqual(0d, downstream.Viz.DesiredRate, "Rate propagation must not enable overlap trimming.");
    }

    private sealed class RateProbe(string name) : BaseBlock(name)
    {
        public MOSAIC.Visualization.BlockVisualization Viz { get; } = new();
        protected override MOSAIC.Visualization.BlockVisualization? Visualization => Viz;
        public void ChangeRate(double rate) => UpdateAndPropagateRate(rate);
        protected override void OnReceive(object sender, object value) { }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Viz.Dispose();
            base.Dispose(disposing);
        }
    }

    [TestMethod]
    public void RemovingADuplicateSubscription_StopsRatePropagation()
    {
        using var source = new RateProbe("source");
        using var downstream = new RateProbe("downstream");
        source.DesiredRate = 100;
        source.AddSubscriber(downstream);
        source.AddSubscriber(downstream);
        source.RemoveSubscriber(downstream);

        source.ChangeRate(200);

        Assert.AreEqual(100d, downstream.DesiredRate,
            "A disconnected block must not receive rate changes through a stale duplicate connection.");
    }

    [TestMethod]
    public void ClearingExplicitScopeRate_UsesTheLatestPublicationRateAndShape()
    {
        using var viz = new MOSAIC.Visualization.BlockVisualization();
        viz.UpdatePublishRate(100);
        viz.Feed(Matrix<double>.Build.Dense(4, 1));
        viz.UpdateSignalRate(2000);
        viz.UpdatePublishRate(50);
        viz.Feed(Matrix<double>.Build.Dense(6, 1));

        viz.UpdateSignalRate(0);

        Assert.AreEqual(300d, viz.EffectiveSignalRate,
            "Returning to automatic rate must use the latest 50 packets/s with 6 rows each.");
    }

    /// <summary>
    /// A 100 Hz clock driving one <c>SinGenerator</c>, optionally followed by a window.
    /// </summary>
    /// <remarks>
    /// The clock carries the only <c>DesiredRate</c>, so the generator has to inherit its rate
    /// through the graph — the same path a real pipeline uses, and the path that has to be in
    /// place for the rate to reach the scope at all.
    /// </remarks>
    private static string Pipeline(int scansPerPacket, bool withWindow = false)
    {
        var window = withWindow
            ? """, "window": { "Type": "SlidingWindow", "Inputs": ["gen"], "Params": [200, 100, "Rectangular"] }"""
            : "";

        return $$"""
        {
          "clock": { "Type": "Clock", "DesiredRate": 100 },
          "gen":   { "Type": "SinGenerator", "Inputs": ["clock"],
                     "Params": [4, 1.0, 1.0, 0.0, 0.0, {{scansPerPacket}}] }
          {{window}}
        }
        """;
    }

    /// <summary>Builds a pipeline from JSON and disposes every block it created.</summary>
    private sealed class Graph : IDisposable
    {
        private readonly BlockGraphBuilder.BuiltGraph _graph;

        public Graph(string json)
        {
            var models = JsonParser.Parse(json)
                .ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Key });

            var sp = new ServiceCollection().BuildServiceProvider();
            _graph = BlockGraphBuilder.Build(models, new BlockFactory(sp));
        }

        public T Block<T>(string name) where T : class
        {
            Assert.IsTrue(_graph.Instances.TryGetValue(name, out var instance),
                $"the pipeline has no block called '{name}'");
            Assert.IsInstanceOfType(instance, typeof(T));
            return (T)instance!;
        }

        public void Dispose()
        {
            foreach (var instance in _graph.Instances.Values)
                (instance as IDisposable)?.Dispose();
        }
    }
}
