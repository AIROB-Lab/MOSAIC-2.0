using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.ViewModels.Devices;
using BodyRigBlock = MOSAIC.Models.Devices.BodyRig;

namespace MOSAIC.Tests.ViewModels.Devices;

/// <summary>
/// Covers the card falling back to the calibration profiles shipped beside the executable.
/// </summary>
/// <remarks>
/// <para>
/// Those profiles are deployed as a real folder rather than embedded under <c>Assets</c>: the
/// card lists them with <c>Directory.GetFiles</c> and Store writes new ones back, and neither
/// works against a resource compiled into the assembly.
/// </para>
/// <para>
/// <see cref="DoNotParallelizeAttribute"/> because these swap a process-wide static. The rest of
/// the suite runs method-level parallel and builds cards of its own, so mutating the fallback
/// while those run would hand them a folder they never asked for.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public class BodyRigDefaultProfileDirectoryTests
{
    private string? _saved;
    private readonly List<string> _tempDirs = [];

    [TestInitialize]
    public void SaveDefault() => _saved = BodyRigViewModel.DefaultProfileDirectory;

    [TestCleanup]
    public void Restore()
    {
        BodyRigViewModel.DefaultProfileDirectory = _saved;

        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A stand-in for the shipped folder, holding one loadable profile.</summary>
    private string ShippedFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mosaic-shipped-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        File.WriteAllLines(Path.Combine(dir, "double_hand"), ["0.30:0:0:1:0:0:0:-1:0"]);
        return dir;
    }

    private string EmptyFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mosaic-configured-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [TestMethod]
    public void AnUnconfiguredCard_OpensOnTheShippedProfiles()
    {
        var shipped = ShippedFolder();
        BodyRigViewModel.DefaultProfileDirectory = shipped;

        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block);

        Assert.AreEqual(shipped, vm.CalibrationDirectory);
        Assert.HasCount(1, vm.Profiles, "the shipped profile is listed without typing a path");
        Assert.AreEqual(Path.Combine(shipped, "double_hand"), vm.SelectedProfile);
    }

    [TestMethod]
    public void TheFallbackReachesTheBlock_SoASavedPipelineRecordsIt()
    {
        // Filling only the text box would leave the block unaware, and the folder would be lost
        // the moment the pipeline was saved and reopened.
        var shipped = ShippedFolder();
        BodyRigViewModel.DefaultProfileDirectory = shipped;

        using var block = new BodyRigBlock();
        _ = new BodyRigViewModel(block);

        Assert.AreEqual(shipped, block.CalibrationDirectory);
        Assert.AreEqual(shipped, block.ToJsonModel().Params![2]);
        Assert.AreEqual("double_hand", block.ToJsonModel().Params![3]);
    }

    [TestMethod]
    public void AFolderNamedByThePipeline_WinsOverTheShippedDefault()
    {
        // The fallback fills a blank; it never overrides a deliberate choice.
        var shipped = ShippedFolder();
        var configured = EmptyFolder();
        BodyRigViewModel.DefaultProfileDirectory = shipped;

        using var block = BodyRigBlock.ConfigureInput(
            new ServiceCollection().BuildServiceProvider(),
            new JsonModel { Type = "BodyRig", Name = "rig", Params = ["", 3, configured] });

        var vm = new BodyRigViewModel(block);

        Assert.AreEqual(configured, vm.CalibrationDirectory);
        Assert.IsEmpty(vm.Profiles, "the configured folder is empty, and that is what was asked for");
    }

    [TestMethod]
    public void WithNothingShipped_TheCardOpensBlankAsBefore()
    {
        BodyRigViewModel.DefaultProfileDirectory = null;

        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block);

        Assert.AreEqual(string.Empty, vm.CalibrationDirectory);
        Assert.AreEqual("Set a profile directory first", vm.StoreTargetHint);
    }

    [TestMethod]
    public void AFolderThatVanishedAfterStartup_OffersNoProfiles()
    {
        // FindShippedProfiles only returns an existing folder, so this is the case where the
        // folder disappears after startup. The path stays in the box; what must not happen is
        // the card offering a profile that is not there.
        BodyRigViewModel.DefaultProfileDirectory =
            Path.Combine(Path.GetTempPath(), "mosaic-absent-" + Guid.NewGuid().ToString("N"));

        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block);

        // The block rescans and finds nothing; the card must not claim a usable profile.
        Assert.IsEmpty(vm.Profiles);
        Assert.IsNull(block.CalibrationName);
    }
}
