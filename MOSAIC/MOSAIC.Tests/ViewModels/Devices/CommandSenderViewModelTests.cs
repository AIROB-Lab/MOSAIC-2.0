using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Models.Devices;
using MOSAIC.ViewModels.Devices;

namespace MOSAIC.Tests.ViewModels.Devices;

/// <summary>
/// Tests for <see cref="CommandSenderViewModel"/> — the ESP command card.
/// </summary>
/// <remarks>
/// The card is declarative: the checkboxes are the desired device state, and Apply drives the
/// device to exactly that state. The tests that matter most are the ones proving an un-ticked
/// stream produces an explicit <c>off</c> — the previous design only sent commands for ticked
/// streams, so un-ticking one did nothing and "off" looked broken.
/// </remarks>
[TestClass]
public class CommandSenderViewModelTests
{
    private static CommandSender Block(string host = "127.0.0.1", int port = 11000) =>
        CommandSender.ConfigureInput(
            new ServiceCollection().BuildServiceProvider(),
            new JsonModel { Type = "CommandSender", Name = "ESP-Control", Params = [host, port] });

    private static void Tick(CommandSenderViewModel vm, EspStreamType type) =>
        vm.StreamOptions.First(o => o.Type == type).IsSelected = true;

    // ── Connection ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Constructor_ShowsTheBlocksAlreadyConnectedState()
    {
        // The block connects during ConfigureInput, so the card must not open on a
        // stale "Disconnected" that makes the user press Connect for no reason.
        using var block = Block();

        var vm = new CommandSenderViewModel(block);

        Assert.IsTrue(vm.IsConnected);
        Assert.AreEqual("127.0.0.1", vm.Host);
        Assert.AreEqual("11000", vm.Port);
    }

    [TestMethod]
    public void Disconnect_ClearsTheConnectedState()
    {
        using var block = Block();
        var vm = new CommandSenderViewModel(block);

        vm.DisconnectCommand.Execute(null);

        Assert.IsFalse(vm.IsConnected);
        Assert.IsFalse(block.IsConnected);
    }

    [TestMethod]
    public void Connect_WithAnInvalidPort_ReportsItAndStaysDisconnected()
    {
        var vm = new CommandSenderViewModel { Port = "99999" };

        vm.ConnectCommand.Execute(null);

        Assert.IsFalse(vm.IsConnected);
        Assert.AreEqual("Invalid port number.", vm.StatusMessage);
    }

    // ── Stream options ──────────────────────────────────────────────────────

    [TestMethod]
    public void StreamOptions_AreTheFourRealStreams_WithoutAll()
    {
        // ALL as a checkbox beside the individual streams describes no coherent state
        // (ALL ticked + ROT un-ticked?). Ticking all four says the same thing cleanly.
        var vm = new CommandSenderViewModel();

        Assert.HasCount(4, vm.StreamOptions);
        CollectionAssert.AreEquivalent(
            new[] { EspStreamType.ROT, EspStreamType.ACC, EspStreamType.GYR, EspStreamType.MAG },
            vm.StreamOptions.Select(o => o.Type).ToArray());
    }

    [TestMethod]
    public void StreamOptions_StartAllUnticked()
    {
        var vm = new CommandSenderViewModel();

        Assert.IsFalse(vm.StreamOptions.Any(o => o.IsSelected));
    }

    [TestMethod]
    public void SelectAll_And_ClearAll_OnlyTouchTheCheckboxes()
    {
        var vm = new CommandSenderViewModel();

        vm.SelectAllStreamsCommand.Execute(null);
        Assert.IsTrue(vm.StreamOptions.All(o => o.IsSelected));

        vm.ClearAllStreamsCommand.Execute(null);
        Assert.IsFalse(vm.StreamOptions.Any(o => o.IsSelected));
    }

    // ── Command preview ─────────────────────────────────────────────────────

    [TestMethod]
    public void CommandPreview_SpellsOutOnAndOffForEveryStream()
    {
        // The whole point: the un-ticked streams are visibly going to be turned off.
        var vm = new CommandSenderViewModel { DeviceSpec = "18" };
        Tick(vm, EspStreamType.ROT);

        Assert.Contains("ROT18on", vm.CommandPreview);
        Assert.Contains("ACC18off", vm.CommandPreview);
        Assert.Contains("GYR18off", vm.CommandPreview);
        Assert.Contains("MAG18off", vm.CommandPreview);
    }

    [TestMethod]
    public void CommandPreview_WithNothingTicked_TurnsEverythingOff()
    {
        var vm = new CommandSenderViewModel { DeviceSpec = "18" };

        foreach (var type in new[] { "ROT", "ACC", "GYR", "MAG" })
            Assert.Contains($"{type}18off", vm.CommandPreview);
    }

    [TestMethod]
    public void CommandPreview_RaisesChangeWhenAStreamIsTicked()
    {
        // The checkboxes are separate observable objects; without an explicit
        // subscription the preview would silently go stale.
        var vm = new CommandSenderViewModel { DeviceSpec = "18" };
        bool raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CommandPreview)) raised = true;
        };

        Tick(vm, EspStreamType.ROT);

        Assert.IsTrue(raised, "ticking a stream did not refresh the command preview");
    }

    [TestMethod]
    public void CommandPreview_RaisesChangeWhenTheDeviceNumberChanges()
    {
        var vm = new CommandSenderViewModel();
        Tick(vm, EspStreamType.ROT);

        bool raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CommandPreview)) raised = true;
        };

        vm.DeviceSpec = "18";

        Assert.IsTrue(raised);
        Assert.Contains("ROT18on", vm.CommandPreview);
    }

    // ── Apply ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void Apply_SendsACommandForEveryStream_NotJustTheTickedOnes()
    {
        // Four streams => four commands, so an un-ticked stream is explicitly stopped.
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "18" };
        Tick(vm, EspStreamType.ROT);

        vm.ApplyCommand.Execute(null);

        Assert.AreEqual(4, block.Info.CommandsSent);
    }

    [TestMethod]
    public void Apply_LastCommandReflectsTheFinalStreamsState()
    {
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "18" };
        // MAG is last in the list and left un-ticked, so it must go out as "off".
        Tick(vm, EspStreamType.ROT);

        vm.ApplyCommand.Execute(null);

        Assert.AreEqual("MAG18off", block.Info.LastCommand);
    }

    [TestMethod]
    public void Apply_WithEverythingTicked_TurnsEveryStreamOn()
    {
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "7" };
        vm.SelectAllStreamsCommand.Execute(null);

        vm.ApplyCommand.Execute(null);

        Assert.AreEqual(4, block.Info.CommandsSent);
        Assert.AreEqual("MAG7on", block.Info.LastCommand);
        Assert.Contains("on, rest off", vm.StatusMessage);
    }

    [TestMethod]
    public void Apply_AfterEditingTheHost_RebindsToTheAddressOnTheCard()
    {
        // Host reaches the block only through Connect(), so Apply used to transmit to the
        // previously bound endpoint while the card displayed the new one — and still report
        // success, naming the right device number in the status line.
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "18" };

        vm.Host = "127.0.0.2";
        vm.ApplyCommand.Execute(null);

        Assert.AreEqual("127.0.0.2", block.Host);
        Assert.AreEqual("127.0.0.2:11000", block.Info.Target);
        Assert.AreEqual(4, block.Info.CommandsSent);
    }

    [TestMethod]
    public void Apply_AfterEditingThePort_RebindsToThePortOnTheCard()
    {
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "18" };

        vm.Port = "11001";
        vm.ApplyCommand.Execute(null);

        Assert.AreEqual(11001, block.Port);
        Assert.AreEqual("127.0.0.1:11001", block.Info.Target);
        Assert.AreEqual(4, block.Info.CommandsSent);
    }

    [TestMethod]
    public void Apply_WithAnInvalidPortOnTheCard_SendsNothing()
    {
        // The previous endpoint is still bound and IsConnected is still true, so falling
        // through here would send to an address the card is no longer showing.
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "18" };

        vm.Port = "99999";
        vm.ApplyCommand.Execute(null);

        Assert.AreEqual(0, block.Info.CommandsSent);
        Assert.AreEqual("Invalid port number.", vm.StatusMessage);
    }

    [TestMethod]
    public void Apply_WithAnUntouchedEndpoint_SendsWithoutRebinding()
    {
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "18" };

        vm.ApplyCommand.Execute(null);

        Assert.AreEqual("127.0.0.1:11000", block.Info.Target);
        Assert.AreEqual(4, block.Info.CommandsSent);
    }

    [TestMethod]
    public void Apply_WithNothingTicked_ReportsAllStreamsOff()
    {
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "18" };

        vm.ApplyCommand.Execute(null);

        Assert.AreEqual(4, block.Info.CommandsSent);
        Assert.Contains("Dev 18: all streams off", vm.StatusMessage);
    }

    [TestMethod]
    public void Apply_WhenNotConnected_SaysSoAndSendsNothing()
    {
        var vm = new CommandSenderViewModel();
        Tick(vm, EspStreamType.ROT);

        vm.ApplyCommand.Execute(null);

        Assert.AreEqual("Not connected — press Connect first.", vm.StatusMessage);
    }
}
