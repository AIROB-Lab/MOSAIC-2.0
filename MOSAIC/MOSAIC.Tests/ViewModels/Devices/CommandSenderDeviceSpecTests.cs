using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Models.Devices;
using MOSAIC.ViewModels.Devices;

namespace MOSAIC.Tests.ViewModels.Devices;

/// <summary>
/// Tests for the multi-device targeting on <see cref="CommandSenderViewModel"/> — the device
/// spec parser and the fan-out that Apply performs across every addressed node.
/// </summary>
/// <remarks>
/// A body rig runs many ESP nodes at once and their ids are sparse, so the card takes a spec
/// like <c>"1-8, 18"</c> rather than one number. Apply then sends one command per
/// (device × stream) pair, which is what makes a whole rig configurable in a single action.
/// </remarks>
[TestClass]
public class CommandSenderDeviceSpecTests
{
    private static CommandSender Block() =>
        CommandSender.ConfigureInput(
            new ServiceCollection().BuildServiceProvider(),
            new JsonModel { Type = "CommandSender", Name = "ESP-Control", Params = ["127.0.0.1", 11000] });

    private static int[] Parse(string spec)
    {
        Assert.IsTrue(CommandSenderViewModel.TryParseDevices(spec, out var d, out var err),
            $"'{spec}' failed to parse: {err}");
        return d.ToArray();
    }

    // ── Parser: valid specs ─────────────────────────────────────────────────

    [TestMethod]
    public void ParsesASingleDevice()
    {
        CollectionAssert.AreEqual(new[] { 18 }, Parse("18"));
    }

    [TestMethod]
    public void ParsesACommaSeparatedList()
    {
        CollectionAssert.AreEqual(new[] { 1, 2, 5 }, Parse("1,2,5"));
    }

    [TestMethod]
    public void ParsesAnInclusiveRange()
    {
        CollectionAssert.AreEqual(new[] { 3, 4, 5, 6 }, Parse("3-6"));
    }

    [TestMethod]
    public void ParsesRangesAndSinglesTogether()
    {
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 18 }, Parse("1-8, 18"));
    }

    [TestMethod]
    public void ToleratesSpacesSemicolonsAndTabs()
    {
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, Parse(" 1 ; 2\t3 "));
    }

    [TestMethod]
    public void SortsAndDeduplicates()
    {
        // Overlapping ranges are natural to type and must not send a node two commands.
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 9 }, Parse("9, 3-4, 1-3, 2"));
    }

    [TestMethod]
    public void AcceptsAReversedRange()
    {
        // "8-1" is a typo, not a request for nothing.
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Parse("8-1"));
    }

    [TestMethod]
    public void AcceptsTheFullValidRange()
    {
        Assert.HasCount(99, Parse("1-99"));
    }

    // ── Parser: rejections ──────────────────────────────────────────────────

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public void RejectsAnEmptySpec(string? spec)
    {
        Assert.IsFalse(CommandSenderViewModel.TryParseDevices(spec, out var d, out var err));
        Assert.IsEmpty(d);
        Assert.AreEqual("no device specified", err);
    }

    [TestMethod]
    public void RejectsNonNumericTokens_AndNamesTheOffender()
    {
        Assert.IsFalse(CommandSenderViewModel.TryParseDevices("1, abc, 3", out _, out var err));
        Assert.Contains("abc", err!);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("100")]
    [DataRow("1-100")]
    public void RejectsOutOfRangeIds(string spec)
    {
        Assert.IsFalse(CommandSenderViewModel.TryParseDevices(spec, out _, out var err));
        Assert.Contains("outside 1-99", err!);
    }

    // ── ViewModel surface ───────────────────────────────────────────────────

    [TestMethod]
    public void DeviceCount_TracksTheSpec()
    {
        var vm = new CommandSenderViewModel { DeviceSpec = "1-8, 18" };

        Assert.AreEqual(9, vm.DeviceCount);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 18 }, vm.Devices.ToArray());
    }

    [TestMethod]
    public void DeviceCount_IsZeroForAMalformedSpec()
    {
        var vm = new CommandSenderViewModel { DeviceSpec = "nonsense" };

        Assert.AreEqual(0, vm.DeviceCount);
        Assert.IsEmpty(vm.Devices);
    }

    [TestMethod]
    public void CommandPreview_ForOneDevice_ListsTheLiteralCommands()
    {
        var vm = new CommandSenderViewModel { DeviceSpec = "18" };
        vm.StreamOptions.First(o => o.Type == EspStreamType.ROT).IsSelected = true;

        Assert.Contains("ROT18on", vm.CommandPreview);
        Assert.Contains("MAG18off", vm.CommandPreview);
    }

    [TestMethod]
    public void CommandPreview_ForManyDevices_SummarisesInsteadOfListing()
    {
        // 9 devices x 4 streams = 36 commands; spelling them all out helps nobody.
        var vm = new CommandSenderViewModel { DeviceSpec = "1-8, 18" };
        vm.SelectAllStreamsCommand.Execute(null);

        var preview = vm.CommandPreview;
        Assert.Contains("9 devices", preview);
        Assert.Contains("1-8,18", preview, "the id list should collapse runs");
        Assert.Contains("36 commands", preview);
    }

    [TestMethod]
    public void CommandPreview_ForAMalformedSpec_ShowsTheError()
    {
        var vm = new CommandSenderViewModel { DeviceSpec = "1-abc" };

        Assert.Contains("⚠", vm.CommandPreview);
    }

    [TestMethod]
    public void CommandPreview_RefreshesWhenTheSpecChanges()
    {
        var vm = new CommandSenderViewModel { DeviceSpec = "1" };
        bool raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CommandPreview)) raised = true;
        };

        vm.DeviceSpec = "1-8";

        Assert.IsTrue(raised);
    }

    // ── Apply across many devices ───────────────────────────────────────────

    [TestMethod]
    public void Apply_SendsEveryStreamToEveryDevice()
    {
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "1-8, 18" };
        vm.StreamOptions.First(o => o.Type == EspStreamType.ROT).IsSelected = true;

        vm.ApplyCommand.Execute(null);

        Assert.AreEqual(9 * 4, block.Info.CommandsSent, "9 devices x 4 streams");
    }

    [TestMethod]
    public void Apply_ReportsTheDeviceCountAndCommandTotal()
    {
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "1-3" };
        vm.SelectAllStreamsCommand.Execute(null);

        vm.ApplyCommand.Execute(null);

        Assert.Contains("3 devices", vm.StatusMessage);
        Assert.Contains("12 commands", vm.StatusMessage);
    }

    [TestMethod]
    public void Apply_ForASingleDevice_StillNamesItDirectly()
    {
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "18" };

        vm.ApplyCommand.Execute(null);

        Assert.Contains("Dev 18", vm.StatusMessage);
    }

    [TestMethod]
    public void Apply_WithAMalformedSpec_ReportsItAndSendsNothing()
    {
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "1-abc" };

        vm.ApplyCommand.Execute(null);

        Assert.AreEqual(0, block.Info.CommandsSent);
        Assert.Contains("abc", vm.StatusMessage);
    }

    [TestMethod]
    public void Apply_TheLastCommandTargetsTheHighestDevice()
    {
        using var block = Block();
        var vm = new CommandSenderViewModel(block) { DeviceSpec = "1, 18" };

        vm.ApplyCommand.Execute(null);

        // Devices are visited in ascending order, streams in list order (MAG last).
        Assert.AreEqual("MAG18off", block.Info.LastCommand);
    }
}
