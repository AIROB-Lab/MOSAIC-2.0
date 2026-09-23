using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Factory;
using MOSAIC.Models.Devices;
using MOSAIC.Models.FlowControl;
using MOSAIC.Tests.TestSupport;
using Buffer = MOSAIC.Models.FlowControl.Buffer;

namespace MOSAIC.Tests.Models.Devices;

/// <summary>
/// Tests for <see cref="DlrAdcBt"/> — the DLR ADC/BT acquisition block ported from iM-Blocks.
/// </summary>
/// <remarks>
/// The serial port itself is not exercised: it needs a paired Bluetooth dongle. What is covered
/// is everything downstream of the wire — the frame parser, its resync and error paths, the
/// count-to-volts conversion, and the sample-and-hold publishing contract. Bytes are pushed in
/// through <see cref="DlrAdcBt.Ingest"/>, which is the same entry point the reader thread uses.
/// </remarks>
[TestClass]
public class DlrAdcBtTests
{
    private const int HeaderBytes  = 4;
    private const int TrailerBytes = 4;

    private static IServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    private static JsonModel Model(params object[] parameters) =>
        new() { Type = "DlrAdcBt", Name = "DlrAdc", Params = parameters };

    /// <summary>Frame size for a channel count, matching the block's own arithmetic.</summary>
    private static int FrameSize(int channels) => HeaderBytes + 2 * channels + TrailerBytes;

    /// <summary>
    /// Builds one wire frame carrying <paramref name="counts"/>, one big-endian uint16 per channel.
    /// </summary>
    private static byte[] Frame(params int[] counts)
    {
        int size = FrameSize(counts.Length);
        var frame = new byte[size];

        frame[0] = 0xAA;
        frame[1] = 0x7A;
        frame[2] = (byte)size;
        frame[3] = 0x00;                 // command echo, ignored by the parser

        for (int i = 0; i < counts.Length; i++)
        {
            frame[HeaderBytes + 2 * i]     = (byte)(counts[i] >> 8);
            frame[HeaderBytes + 2 * i + 1] = (byte)(counts[i] & 0xFF);
        }

        frame[size - 2] = 0xFF;
        frame[size - 1] = 0xFF;
        return frame;
    }

    /// <summary>The block's count-to-volts conversion, restated so a change to it fails a test.</summary>
    private static double Volts(int count) => 5.0 * count / 4000.0;

    /// <summary>Ticks the block once and returns what it published.</summary>
    private static Vector<double> Tick(DlrAdcBt block) =>
        BlockHarness.CaptureVector(block, 0.0);

    private static byte[] Concat(params byte[][] chunks)
    {
        var all = new List<byte>();
        foreach (var chunk in chunks) all.AddRange(chunk);
        return all.ToArray();
    }

    #region Configuration

    [TestMethod]
    public void ConfigureInput_ReadsPortChannelsAndBaud()
    {
        using var block = DlrAdcBt.ConfigureInput(Services(), Model("COM7", 8, 57600));

        Assert.AreEqual("COM7", block.PortNumber);
        Assert.AreEqual(8, block.NumOfChannels);
        Assert.AreEqual(57600, block.BaudRate);
    }

    [TestMethod]
    public void ConfigureInput_WithNoParams_UsesDefaults()
    {
        using var block = DlrAdcBt.ConfigureInput(Services(), Model());

        Assert.AreEqual(string.Empty, block.PortNumber);
        Assert.AreEqual(10, block.NumOfChannels);
        Assert.AreEqual(115200, block.BaudRate);
    }

    [TestMethod]
    public void PortNumber_AcceptsABareNumber()
    {
        // The old control panel's spin box produced "5", not "COM5"; a config written from it
        // must still open the right port.
        using var block = new DlrAdcBt();
        block.PortNumber = "5";

        Assert.AreEqual("COM5", block.PortNumber);
    }

    [TestMethod]
    public void BytesPerFrame_TracksTheChannelCount()
    {
        using var block = new DlrAdcBt { NumOfChannels = 10 };
        Assert.AreEqual(4 + 20 + 4, block.BytesPerFrame);

        block.NumOfChannels = 3;
        Assert.AreEqual(4 + 6 + 4, block.BytesPerFrame);
    }

    [TestMethod]
    public void NumOfChannels_IsClampedToWhatACommandByteCanCarry()
    {
        using var block = new DlrAdcBt();

        block.NumOfChannels = 0;
        Assert.AreEqual(1, block.NumOfChannels);

        block.NumOfChannels = 9999;
        Assert.AreEqual(255, block.NumOfChannels);
    }

    [TestMethod]
    public void RoundTripsThroughJson()
    {
        using var block = DlrAdcBt.ConfigureInput(Services(), Model("COM7", 4, 9600));
        var json = block.ToJsonModel();

        Assert.AreEqual("DlrAdcBt", json.Type);
        CollectionAssert.AreEqual(new object[] { "COM7", 4, 9600 }, (System.Collections.ICollection)json.Params!);
    }

    [TestMethod]
    [DataRow("DlrAdcBt")]
    [DataRow("dlradcbt")]
    [DataRow("DLR_ADCBT")]   // the iM-Blocks type name, so old pipeline configs still load
    [DataRow("dlradc")]
    public void TheFactoryResolvesEveryTypeAlias(string type)
    {
        var created = new BlockFactory(Services()).Create(
            new JsonModel { Type = type, Name = "DlrAdc", Params = ["COM7", 4] });

        var block = (DlrAdcBt)created;
        using (block)
        {
            Assert.AreEqual(4, block.NumOfChannels);
        }
    }

    [TestMethod]
    public void ThePaletteEntryMatchesTheBlock()
    {
        // The catalog is hand-written while the constraints are reflected off the class, so this
        // is where the two can drift: a block listed as a source would be offered with no clock.
        Assert.IsTrue(BlockCatalog.ByKey.TryGetValue("dlradcbt", out var descriptor));
        Assert.AreEqual(typeof(DlrAdcBt), descriptor!.BlockType);
        Assert.AreEqual(BlockCategory.Devices, descriptor.Category);
        Assert.IsFalse(descriptor.CanBeSource, "The block needs a clock; the palette must not offer it as a source.");

        var constraints = BlockConstraints.For(typeof(DlrAdcBt));
        Assert.AreEqual(1, constraints.Min);
        Assert.AreEqual(1, constraints.Max);
        Assert.IsTrue(BlockConstraints.Accepts(typeof(DlrAdcBt), nameof(ClockBlock)));
        Assert.IsFalse(BlockConstraints.Accepts(typeof(DlrAdcBt), nameof(Buffer)));
    }

    [TestMethod]
    public void OnlyAcceptsAClockAsInput()
    {
        // The block is polled, not pushed: without a clock nothing ever decides when a frame is
        // taken off the buffer, so the palette must not let anything else feed it.
        using var block = new DlrAdcBt();

        Assert.AreEqual(1, block.MinInputs);
        Assert.AreEqual(1, block.MaxInputs);
        CollectionAssert.AreEqual(new[] { nameof(ClockBlock) }, block.AllowableBlocks);
    }

    /// <summary>
    /// The shipped example config, verbatim. Kept inline rather than read from disk so the test
    /// does not depend on the build's asset layout.
    /// </summary>
    private const string ExampleConfig =
        """
        // Comments are allowed by the pipeline parser; keep one here so that stays true.
        {
          "timer": {
            "Type": "ClockBlock",
            "DesiredRate": 100
          },

          "dlrAdc": {
            "Type": "DlrAdcBt",
            "Inputs": ["timer"],
            "Params": ["COM7", 10, 115200]
          }
        }
        """;

    [TestMethod]
    public void TheExampleConfigBuildsAndWiresTheWholeGraph()
    {
        var models = JsonParser.Parse(ExampleConfig)
            .ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Key });

        var graph = BlockGraphBuilder.Build(models, new BlockFactory(Services()));
        try
        {
            var block = (DlrAdcBt)graph.Instances["dlrAdc"];

            Assert.IsInstanceOfType<ClockBlock>(graph.Instances["timer"]);
            Assert.AreEqual("COM7", block.PortNumber);
            Assert.AreEqual(10, block.NumOfChannels);
            Assert.AreEqual(115200, block.BaudRate);

            // The clock's rate has to reach the block, or the pipeline runs at zero.
            Assert.AreEqual(100, block.DesiredRate, 1e-9);

            // Loading a config must not touch hardware — nothing may open until Connect.
            Assert.IsFalse(block.IsPortOpen);
            Assert.IsFalse(block.IsConnected);
        }
        finally
        {
            foreach (var instance in graph.Instances.Values) (instance as IDisposable)?.Dispose();
        }
    }

    [TestMethod]
    public void APortGivenAsABareNumberInJsonStillResolves()
    {
        // Params come through as JsonElement, so a numeric 7 arrives as a number, not a string.
        var models = JsonParser.Parse(
                """
                {
                  "timer":  { "Type": "ClockBlock", "DesiredRate": 50 },
                  "dlrAdc": { "Type": "DlrAdcBt", "Inputs": ["timer"], "Params": [7, 4] }
                }
                """)
            .ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Key });

        var graph = BlockGraphBuilder.Build(models, new BlockFactory(Services()));
        try
        {
            var block = (DlrAdcBt)graph.Instances["dlrAdc"];
            Assert.AreEqual("COM7", block.PortNumber);
            Assert.AreEqual(4, block.NumOfChannels);
            Assert.AreEqual(115200, block.BaudRate, "An omitted baud must keep the default.");
        }
        finally
        {
            foreach (var instance in graph.Instances.Values) (instance as IDisposable)?.Dispose();
        }
    }

    #endregion

    #region Frame Parsing

    [TestMethod]
    public void ATickDecodesOneFrameIntoVolts()
    {
        using var block = new DlrAdcBt { NumOfChannels = 3 };
        block.Ingest(Frame(0, 2000, 4000), FrameSize(3));

        var published = Tick(block);

        Assert.AreEqual(3, published.Count);
        Assert.AreEqual(Volts(0),    published[0], 1e-9);
        Assert.AreEqual(Volts(2000), published[1], 1e-9);
        Assert.AreEqual(Volts(4000), published[2], 1e-9);
        Assert.AreEqual(1, block.Info.FramesProcessed);
    }

    [TestMethod]
    public void ChannelCountsAreBigEndian()
    {
        // 0x0102 is 258, not 513. Getting this backwards would still produce plausible-looking
        // traces, which is exactly why it is asserted rather than eyeballed.
        using var block = new DlrAdcBt { NumOfChannels = 1 };
        block.Ingest(Frame(0x0102), FrameSize(1));

        Assert.AreEqual(Volts(258), Tick(block)[0], 1e-9);
    }

    [TestMethod]
    public void HighBytesSurviveTheBuffer()
    {
        // The original driver decoded with Windows-1252 and re-encoded with Encoding.Default,
        // which corrupts every byte >= 0x80 on .NET Core — most of a full-scale reading.
        using var block = new DlrAdcBt { NumOfChannels = 2 };
        block.Ingest(Frame(0xFFFE, 0x80FF), FrameSize(2));

        var published = Tick(block);
        Assert.AreEqual(Volts(0xFFFE), published[0], 1e-9);
        Assert.AreEqual(Volts(0x80FF), published[1], 1e-9);
    }

    [TestMethod]
    public void ConsecutiveTicksDecodeConsecutiveFrames()
    {
        using var block = new DlrAdcBt { NumOfChannels = 1 };
        block.Ingest(Concat(Frame(100), Frame(200), Frame(300)), 3 * FrameSize(1));

        Assert.AreEqual(Volts(100), Tick(block)[0], 1e-9);
        Assert.AreEqual(Volts(200), Tick(block)[0], 1e-9);
        Assert.AreEqual(Volts(300), Tick(block)[0], 1e-9);
        Assert.AreEqual(3, block.Info.FramesProcessed);
    }

    [TestMethod]
    public void AFrameSplitAcrossTwoReadsIsStillDecoded()
    {
        // Bluetooth SPP delivers whatever the radio had; frame boundaries mean nothing to it.
        var frame = Frame(1234);
        using var block = new DlrAdcBt { NumOfChannels = 1 };

        var head = new byte[3];
        var tail = new byte[frame.Length - 3];
        Array.Copy(frame, 0, head, 0, head.Length);
        Array.Copy(frame, 3, tail, 0, tail.Length);

        block.Ingest(head, head.Length);
        Assert.AreEqual(0.0, Tick(block)[0], 1e-9);   // nothing complete yet

        block.Ingest(tail, tail.Length);
        Assert.AreEqual(Volts(1234), Tick(block)[0], 1e-9);
    }

    [TestMethod]
    public void LeadingGarbageIsSkippedToReachTheFrame()
    {
        using var block = new DlrAdcBt { NumOfChannels = 1 };
        block.Ingest(Concat([0x11, 0x22, 0x33], Frame(777)), 3 + FrameSize(1));

        Assert.AreEqual(Volts(777), Tick(block)[0], 1e-9);
        Assert.AreEqual(1, block.Info.FramesProcessed);
    }

    #endregion

    #region Error Paths

    [TestMethod]
    public void ABadLengthByteIsRejectedAndTheParserResyncs()
    {
        var corrupt = Frame(111);
        corrupt[2] = 0x63;               // length that disagrees with the configured width

        using var block = new DlrAdcBt { NumOfChannels = 1 };
        block.Ingest(Concat(corrupt, Frame(222)), 2 * FrameSize(1));

        // First tick rejects the corrupt frame; the good one behind it must still be reachable.
        Tick(block);
        Assert.AreEqual(1, block.Info.FrameErrors);
        Assert.AreEqual(0, block.Info.FramesProcessed);

        Assert.AreEqual(Volts(222), Tick(block)[0], 1e-9);
        Assert.AreEqual(1, block.Info.FramesProcessed);
    }

    [TestMethod]
    public void ABadTrailerIsRejectedAndTheFrameIsNotRetried()
    {
        var corrupt = Frame(111);
        corrupt[^1] = 0x00;

        using var block = new DlrAdcBt { NumOfChannels = 1 };
        block.Ingest(Concat(corrupt, Frame(222)), 2 * FrameSize(1));

        Tick(block);
        Assert.AreEqual(1, block.Info.FrameErrors);
        Assert.AreEqual(0, block.Info.FramesProcessed);

        Assert.AreEqual(Volts(222), Tick(block)[0], 1e-9);
    }

    [TestMethod]
    public void PureNoiseIsDiscardedInsteadOfWedgingTheParser()
    {
        // A buffer full of bytes that can never begin a frame has to be dropped, or it sits at
        // the head forever and every later frame is stuck behind it.
        using var block = new DlrAdcBt { NumOfChannels = 1 };

        var noise = new byte[FrameSize(1) * 4];
        Array.Fill(noise, (byte)0x5A);
        block.Ingest(noise, noise.Length);

        Tick(block);
        Assert.IsTrue(block.Info.FrameErrors > 0);

        block.Ingest(Frame(999), FrameSize(1));
        Assert.AreEqual(Volts(999), Tick(block)[0], 1e-9);
    }

    [TestMethod]
    public void AnOverflowingBufferKeepsTheNewestBytes()
    {
        // The parser is clocked, so a stalled clock lets the reader run away. What survives has
        // to be the live signal, not the history.
        using var block = new DlrAdcBt { NumOfChannels = 1 };

        for (int i = 0; i < 64; i++)
            block.Ingest(Frame(i), FrameSize(1));

        Assert.IsTrue(block.Info.FrameErrors > 0, "Overflow should have been recorded.");

        // The buffer holds 16 frames, so the oldest survivor is well past frame 0.
        Assert.IsTrue(Tick(block)[0] > Volts(0), "The oldest frames should have been dropped, not the newest.");
    }

    #endregion

    #region Publishing Contract

    [TestMethod]
    public void ATickWithNoFramePublishesTheLastSample()
    {
        // Sample-and-hold: the downstream rate is the clock rate, so a tick that finds nothing
        // buffered still has to emit, and the honest value to emit is the previous one.
        using var block = new DlrAdcBt { NumOfChannels = 2 };
        block.Ingest(Frame(400, 800), FrameSize(2));

        var first = Tick(block);
        var second = Tick(block);

        Assert.AreEqual(Volts(400), second[0], 1e-9);
        Assert.AreEqual(Volts(800), second[1], 1e-9);
        Assert.AreEqual(1, block.Info.FramesProcessed, "The second tick must not count as a decoded frame.");
        Assert.AreNotSame(first, second, "Each publish must be its own vector, not a shared buffer.");
    }

    [TestMethod]
    public void ATickWithNothingEverReceivedPublishesZeros()
    {
        // The state the block is in whenever the dongle is absent. It must degrade to silence,
        // not throw and take the pipeline down with it.
        using var block = new DlrAdcBt { NumOfChannels = 4 };

        var published = Tick(block);

        Assert.AreEqual(4, published.Count);
        foreach (var value in published) Assert.AreEqual(0.0, value, 1e-9);
    }

    [TestMethod]
    public void ChangingTheChannelCountResizesTheOutput()
    {
        using var block = new DlrAdcBt { NumOfChannels = 2 };
        Assert.AreEqual(2, Tick(block).Count);

        block.NumOfChannels = 5;
        Assert.AreEqual(5, Tick(block).Count);
    }

    [TestMethod]
    public void DisconnectWithoutConnectIsSafe()
    {
        // The card's Disconnect button is reachable in states where no port was ever opened.
        using var block = new DlrAdcBt();

        block.Disconnect();

        Assert.IsFalse(block.IsConnected);
        Assert.IsFalse(block.IsPortOpen);
    }

    [TestMethod]
    public void ConnectWithNoPortConfiguredReportsInsteadOfThrowing()
    {
        using var block = new DlrAdcBt();

        block.Connect();

        Assert.IsFalse(block.IsPortOpen);
        Assert.AreEqual("No port configured", block.Info.Flag);
    }

    #endregion
}
