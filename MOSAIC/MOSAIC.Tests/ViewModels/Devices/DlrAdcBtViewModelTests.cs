using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.Devices;
using MOSAIC.ViewModels.Devices;

namespace MOSAIC.Tests.ViewModels.Devices;

/// <summary>
/// Covers the DLR ADC/BT card's rate readouts.
/// </summary>
/// <remarks>
/// <para>
/// The card shows two rates that are easy to mistake for one another: the block <em>publishes</em>
/// on every clock tick, but only decodes a frame when one has actually arrived. The header chip
/// shows the first and the subtitle the second, so the subtitle has to name which is which — two
/// bare "N Hz" readouts side by side read as a contradiction rather than as two measurements.
/// </para>
/// <para>
/// The port is never opened: <see cref="DlrAdcBt.Ingest"/> feeds synthetic frames and
/// <c>ReceiveInput</c> stands in for the clock, so nothing here needs hardware.
/// </para>
/// </remarks>
[TestClass]
public class DlrAdcBtViewModelTests
{
    private static byte[] Frame(params int[] counts)
    {
        int size = 4 + 2 * counts.Length + 4;
        var frame = new byte[size];
        frame[0] = 0xAA;
        frame[1] = 0x7A;
        frame[2] = (byte)size;

        for (int i = 0; i < counts.Length; i++)
        {
            frame[4 + 2 * i]     = (byte)(counts[i] >> 8);
            frame[4 + 2 * i + 1] = (byte)(counts[i] & 0xFF);
        }

        frame[size - 2] = 0xFF;
        frame[size - 1] = 0xFF;
        return frame;
    }

    /// <summary>A frame sized for whatever channel count <paramref name="block"/> is configured for.</summary>
    /// <remarks>
    /// Derived from the block rather than fixed: a frame built for the wrong channel count is the
    /// wrong length, so the parser never matches it and the test silently measures nothing.
    /// </remarks>
    private static byte[] FrameFor(DlrAdcBt block)
    {
        var counts = new int[block.NumOfChannels];
        Array.Fill(counts, 1000);
        return Frame(counts);
    }

    /// <summary>
    /// Runs <paramref name="ticks"/> clock ticks with a frame available for the first
    /// <paramref name="frames"/> of them, then lets the ViewModel sample the result.
    /// </summary>
    /// <remarks>
    /// Frames are interleaved with ticks rather than queued up front: the receive buffer holds
    /// only 16 frames, so feeding them all in one go would overflow and the card would be
    /// reporting dropped frames instead of the arrival rate under test.
    /// </remarks>
    private static DlrAdcBtViewModel Run(DlrAdcBt block, int frames, int ticks)
    {
        var vm = new DlrAdcBtViewModel(block);
        vm.RefreshLive();                 // establishes the baseline sample

        var frame = FrameFor(block);
        for (int i = 0; i < ticks; i++)
        {
            if (i < frames) block.Ingest(frame, frame.Length);
            block.ReceiveInput(block, 0.0);
        }

        Thread.Sleep(120);                // clear the ViewModel's 50 ms minimum sample interval
        vm.RefreshLive();
        return vm;
    }

    [TestMethod]
    public void TheSubtitleNamesBothRatesDistinctly()
    {
        using var block = new DlrAdcBt("dlrAdc", desiredRate: 100) { NumOfChannels = 10 };
        var vm = Run(block, frames: 20, ticks: 20);

        Assert.IsTrue(vm.IsReceiving);
        StringAssert.Contains(vm.StatusMessage, "in →");
        StringAssert.Contains(vm.StatusMessage, "out");
        StringAssert.Contains(vm.StatusMessage, "10 ch");
    }

    [TestMethod]
    public void ThePublishRateIsTheInheritedClockRate()
    {
        // The subtitle's "out" figure has to be the rate downstream actually sees, which is the
        // clock rate — the block publishes on every tick, decoded frame or not.
        using var block = new DlrAdcBt("dlrAdc", desiredRate: 100) { NumOfChannels = 1 };
        var vm = Run(block, frames: 40, ticks: 80);

        Assert.AreEqual(100, vm.PublishRateHz, 1e-9);
        Assert.AreEqual(40, block.Info.FramesProcessed, "Only the ticks with a frame should decode.");
    }

    [TestMethod]
    public void ADisconnectedCardReportsTheBlockFlag()
    {
        // Nothing arriving and no port open: the subtitle falls back to the block's own status
        // rather than printing rates for a stream that does not exist.
        using var block = new DlrAdcBt("dlrAdc", desiredRate: 100);
        var vm = new DlrAdcBtViewModel(block);

        vm.RefreshLive();

        Assert.IsFalse(vm.IsReceiving);
        Assert.AreEqual(block.Info.Flag, vm.StatusMessage);
    }

    [TestMethod]
    public void AnOpenPortWithNoFramesIsDistinguishedFromNotConnected()
    {
        // "Connected but silent" and "not connected" are different problems, so they must not
        // read the same on the card.
        using var block = new DlrAdcBt("dlrAdc", desiredRate: 100);
        var vm = new DlrAdcBtViewModel(block);

        vm.RefreshLive();
        var disconnected = vm.StatusMessage;

        Assert.IsFalse(disconnected.Contains("open, no frames"),
            "A card that never opened a port must not claim the port is open.");
    }
}
