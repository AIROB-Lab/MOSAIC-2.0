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
/// Covers getting a configured rig back: writing a profile from the card, and reopening a saved
/// pipeline on the calibration it was built with.
/// </summary>
/// <remarks>
/// <para>
/// These replace a set of tests that pinned the behaviour as defects. Between them the defects
/// meant a rig configured once could not be recovered: the first profile in a directory could
/// not be created from inside the app, and a reopened pipeline came back on constructor defaults
/// while the card reported it as configured.
/// </para>
/// <para>
/// The root cause was a single fallback — with no profiles found, the block pointed its current
/// <em>file</em> at the calibration <em>directory</em>. Every store then opened a writer on a
/// directory, threw, and was swallowed. <see cref="RescanProfiles_OnAnEmptyDirectory_LeavesNoCurrentFile"/>
/// guards that specific line.
/// </para>
/// </remarks>
[TestClass]
public class BodyRigPersistenceTests
{
    private readonly List<string> _tempDirs = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mosaic-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static IServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    private BodyRigBlock RigIn(string dir, int channels = 3) =>
        BodyRigBlock.ConfigureInput(Services(),
            new JsonModel { Type = "BodyRig", Name = "rig", Params = ["", channels, dir] });

    // ── The root cause ──────────────────────────────────────────────────────

    [TestMethod]
    public void RescanProfiles_OnAnEmptyDirectory_LeavesNoCurrentFile()
    {
        // The current file must be null, never the directory. Pointing it at the directory is
        // what made StoreCalibration open a StreamWriter on a folder.
        using var block = new BodyRigBlock { CalibrationDirectory = NewTempDir() };

        Assert.IsNull(block.CalibrationName);
        Assert.IsEmpty(block.Profiles);
    }

    [TestMethod]
    public void StoreCalibration_ToADirectoryPath_ReportsFailureInsteadOfSwallowingIt()
    {
        var dir = NewTempDir();
        using var block = new BodyRigBlock();

        Assert.IsFalse(block.StoreCalibration(dir));
    }

    [TestMethod]
    public void StoreCalibration_WithNoFilename_ReportsFailure()
    {
        using var block = new BodyRigBlock();

        Assert.IsFalse(block.StoreCalibration("   "));
    }

    // ── Creating the first profile from the card ────────────────────────────

    [TestMethod]
    public void Store_IntoAnEmptyProfileDirectory_CreatesTheNamedProfile()
    {
        var dir = NewTempDir();
        using var block = RigIn(dir);
        var vm = new BodyRigViewModel(block) { NewProfileName = "seated" };

        vm.StoreCalibrationCommand.Execute(null);

        Assert.IsTrue(File.Exists(Path.Combine(dir, "seated.txt")),
            "the name gets a .txt extension so the profile list stays readable");
        Assert.HasCount(1, vm.Profiles);
        Assert.AreEqual("Stored seated.txt", vm.StatusMessage);
    }

    [TestMethod]
    public void Store_ClearsTheNameBox_AndSelectsWhatItJustWrote()
    {
        // Leaving the selection where it was means the next Load silently reads a different
        // profile than the one just saved.
        var dir = NewTempDir();
        using var block = RigIn(dir);
        var vm = new BodyRigViewModel(block) { NewProfileName = "seated" };

        vm.StoreCalibrationCommand.Execute(null);

        Assert.AreEqual(string.Empty, vm.NewProfileName);
        Assert.AreEqual(Path.Combine(dir, "seated.txt"), vm.SelectedProfile);
        Assert.AreEqual(Path.Combine(dir, "seated.txt"), block.CalibrationName);
    }

    [TestMethod]
    public void Store_WithAnExplicitExtension_KeepsIt()
    {
        var dir = NewTempDir();
        using var block = RigIn(dir);
        var vm = new BodyRigViewModel(block) { NewProfileName = "seated.cal" };

        vm.StoreCalibrationCommand.Execute(null);

        Assert.IsTrue(File.Exists(Path.Combine(dir, "seated.cal")));
    }

    [TestMethod]
    public void Store_WithNoNameAndNoSelection_SaysWhatIsMissing()
    {
        // Previously a silent no-op: the button did nothing and explained nothing.
        var dir = NewTempDir();
        using var block = RigIn(dir);
        var vm = new BodyRigViewModel(block);

        vm.StoreCalibrationCommand.Execute(null);

        Assert.AreEqual("Name the profile to store", vm.StatusMessage);
        Assert.IsEmpty(Directory.GetFiles(dir));
    }

    [TestMethod]
    public void Store_WithNoProfileDirectory_SaysSo()
    {
        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block) { NewProfileName = "seated" };

        vm.StoreCalibrationCommand.Execute(null);

        Assert.AreEqual("Set a profile directory first", vm.StatusMessage);
    }

    [TestMethod]
    public void Store_WithAnIllegalFileName_IsRefusedBeforeTouchingTheDisk()
    {
        var dir = NewTempDir();
        using var block = RigIn(dir);
        var vm = new BodyRigViewModel(block) { NewProfileName = "left/right" };

        vm.StoreCalibrationCommand.Execute(null);

        Assert.AreEqual("Profile name has invalid characters", vm.StatusMessage);
        Assert.IsEmpty(Directory.GetFiles(dir));
    }

    [TestMethod]
    public void Store_WithNoNewNameButAProfileSelected_OverwritesThatProfile()
    {
        var dir = NewTempDir();
        using var block = RigIn(dir);
        var vm = new BodyRigViewModel(block) { NewProfileName = "seated" };
        vm.StoreCalibrationCommand.Execute(null);

        block.SetLinkLength(0, 0.42f, 0, 0);
        vm.StoreCalibrationCommand.Execute(null);      // no new name: overwrite the selection

        Assert.HasCount(1, vm.Profiles, "no second file");
        using var reread = RigIn(dir);
        Assert.AreEqual(0.42f, reread.GetLinkLengths(0).X, 1e-4f);
    }

    [TestMethod]
    public void Load_WithNothingToLoad_SaysSoInsteadOfClaimingSuccess()
    {
        using var block = new BodyRigBlock();
        var vm = new BodyRigViewModel(block);

        vm.LoadCalibrationCommand.Execute(null);

        Assert.AreEqual("No profile to load", vm.StatusMessage);
    }

    // ── Reopening a saved pipeline ──────────────────────────────────────────

    [TestMethod]
    public void ConfigureInput_AppliesTheCalibrationItFinds()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "profile.txt");

        using (var source = new BodyRigBlock())
        {
            source.NumOfChannels = 3;
            source.SetLinkLength(0, 0.25f, 0, 0);
            source.SetSensorIndex(0, 7);
            source.SetParent(1, 0);
            source.StoreCalibration(file);
        }

        using var restored = RigIn(dir);

        Assert.AreEqual(0.25f, restored.GetLinkLengths(0).X, 1e-4f, "stored link length, not the 1 m default");
        Assert.AreEqual(7, restored.GetSensorIndex(0));
        Assert.AreEqual(0, restored.GetParentIndex(1), "the stored parent link is applied");
    }

    [TestMethod]
    public void ConfigureInput_LoadsTheProfileNamedInTheFourthParam()
    {
        // Without the name the directory's first file wins, which is only ever right by accident.
        var dir = NewTempDir();
        File.WriteAllLines(Path.Combine(dir, "a.txt"), ["0.10:0:0:1:0:0:0:-1:0", "0:0:0:1:0:0:0:-1:1"]);
        File.WriteAllLines(Path.Combine(dir, "b.txt"), ["0.20:0:0:1:0:0:0:-1:0", "0:0:0:1:0:0:0:-1:1"]);

        using var restored = BodyRigBlock.ConfigureInput(Services(),
            new JsonModel { Type = "BodyRig", Name = "rig", Params = ["", 2, dir, "b.txt"] });

        Assert.AreEqual(0.20f, restored.GetLinkLengths(0).X, 1e-4f);
        Assert.AreEqual("b.txt", Path.GetFileName(restored.CalibrationName!));
    }

    [TestMethod]
    public void ConfigureInput_WithAProfileNameThatIsGone_FallsBackWithoutThrowing()
    {
        var dir = NewTempDir();
        File.WriteAllLines(Path.Combine(dir, "a.txt"), ["0.10:0:0:1:0:0:0:-1:0"]);

        using var restored = BodyRigBlock.ConfigureInput(Services(),
            new JsonModel { Type = "BodyRig", Name = "rig", Params = ["", 2, dir, "deleted.txt"] });

        Assert.AreEqual("a.txt", Path.GetFileName(restored.CalibrationName!),
            "a renamed or deleted profile falls back to the first, rather than failing to open");
    }

    [TestMethod]
    public void JsonRoundTrip_BringsBackTheWholeConfiguredRig()
    {
        // The end-to-end case: configure once, save the pipeline, reopen it.
        var dir = NewTempDir();
        JsonModel model;

        using (var source = new BodyRigBlock { PortNumber = "COM4", CalibrationDirectory = dir })
        {
            source.NumOfChannels = 3;
            source.SetLinkLength(0, 0.25f, 0.1f, -0.05f);
            source.SetSensorIndex(0, 7);
            source.SetParent(1, 0);
            source.GetSegment(0)!.Name = "rightForeArm";
            source.StoreCalibration(Path.Combine(dir, "seated.txt"));

            model = source.ToJsonModel() with { Name = "rig" };
        }

        using var restored = BodyRigBlock.ConfigureInput(Services(), model);

        Assert.AreEqual("COM4", restored.PortNumber);
        Assert.AreEqual(3, restored.SegmentCount);
        Assert.AreEqual(0.25f, restored.GetLinkLengths(0).X, 1e-4f);
        Assert.AreEqual(0.1f, restored.GetLinkLengths(0).Y, 1e-4f);
        Assert.AreEqual(7, restored.GetSensorIndex(0));
        Assert.AreEqual(0, restored.GetParentIndex(1));
        Assert.AreEqual("rightForeArm", restored.GetSegment(0)!.Name);
    }

    // ── Segment names ───────────────────────────────────────────────────────

    [TestMethod]
    public void SegmentNames_SurviveStoreAndLoad()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "named.txt");

        using (var source = new BodyRigBlock())
        {
            source.NumOfChannels = 2;
            source.GetSegment(0)!.Name = "rightForeArm";
            source.StoreCalibration(file);
        }

        using var restored = new BodyRigBlock();
        restored.NumOfChannels = 2;
        restored.LoadCalibration(file);

        Assert.AreEqual("rightForeArm", restored.GetSegment(0)!.Name);
        Assert.IsNull(restored.GetSegment(1)!.Name, "an unnamed segment stays unnamed");
    }

    [TestMethod]
    public void SegmentNames_MayContainTheFieldSeparator()
    {
        // The name is the last field and the loader rejoins everything from there, so a colon
        // needs no escaping. Worth pinning: it is the reason the name goes last.
        var dir = NewTempDir();
        var file = Path.Combine(dir, "named.txt");

        using (var source = new BodyRigBlock())
        {
            source.NumOfChannels = 1;
            source.GetSegment(0)!.Name = "arm: right, upper";
            source.StoreCalibration(file);
        }

        using var restored = new BodyRigBlock();
        restored.NumOfChannels = 1;
        restored.LoadCalibration(file);

        Assert.AreEqual("arm: right, upper", restored.GetSegment(0)!.Name);
    }

    [TestMethod]
    public void SegmentNames_WithLineBreaks_AreFlattenedRatherThanSplittingTheRecord()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "named.txt");

        using (var source = new BodyRigBlock())
        {
            source.NumOfChannels = 1;
            source.GetSegment(0)!.Name = "right\r\nForeArm";
            source.StoreCalibration(file);
        }

        Assert.HasCount(1, File.ReadAllLines(file), "still one record per segment");

        using var restored = new BodyRigBlock();
        restored.NumOfChannels = 1;
        restored.LoadCalibration(file);

        Assert.AreEqual("right  ForeArm", restored.GetSegment(0)!.Name);
    }

    [TestMethod]
    public void LoadingAProfileWithoutNames_ClearsTheNamesAlreadyInMemory()
    {
        // The file is authoritative: loading profile B must not leave profile A's labels behind.
        var dir = NewTempDir();
        var legacy = Path.Combine(dir, "legacy.txt");
        File.WriteAllLines(legacy, ["0.5:0:0:1:0:0:0:-1:0"]);

        using var block = new BodyRigBlock();
        block.NumOfChannels = 1;
        block.GetSegment(0)!.Name = "staleName";

        block.LoadCalibration(legacy);

        Assert.IsNull(block.GetSegment(0)!.Name);
    }

    [TestMethod]
    public void NineFieldFilesWrittenBeforeNamesExisted_StillLoad()
    {
        var dir = NewTempDir();
        var legacy = Path.Combine(dir, "legacy.txt");
        File.WriteAllLines(legacy, ["0.5:0:0:1:0:0:0:-1:4"]);

        using var block = RigIn(dir, channels: 2);

        Assert.AreEqual(0.5f, block.GetLinkLengths(0).X, 1e-4f);
        Assert.AreEqual(4, block.GetSensorIndex(0));
    }

    // ── What the card says Store will do ────────────────────────────────────

    [TestMethod]
    public void StoreTargetHint_NamesTheFileANewNameWouldWrite()
    {
        var dir = NewTempDir();
        using var block = RigIn(dir);
        var vm = new BodyRigViewModel(block) { NewProfileName = "seated" };

        Assert.AreEqual("Store \u2192 seated.txt", vm.StoreTargetHint);
    }

    [TestMethod]
    public void StoreTargetHint_FallsBackToTheSelectedProfile()
    {
        var dir = NewTempDir();
        using var block = RigIn(dir);
        var vm = new BodyRigViewModel(block) { NewProfileName = "seated" };
        vm.StoreCalibrationCommand.Execute(null);

        // Name box cleared by the store; the selection is now what Store would overwrite.
        Assert.AreEqual("Store \u2192 seated.txt", vm.StoreTargetHint);
    }

    [TestMethod]
    public void StoreTargetHint_ReportsWhatIsMissing_RatherThanAFileName()
    {
        using var noDir = new BodyRigBlock();
        var a = new BodyRigViewModel(noDir) { NewProfileName = "seated" };
        Assert.AreEqual("Set a profile directory first", a.StoreTargetHint);

        var dir = NewTempDir();
        using var block = RigIn(dir);
        var b = new BodyRigViewModel(block);
        Assert.AreEqual("Name the profile to store", b.StoreTargetHint);
    }

    [TestMethod]
    public void StoreTargetHint_RefreshesAsTheNameIsTyped()
    {
        // Bound to a label next to the button, so a stale value would describe the wrong file.
        var dir = NewTempDir();
        using var block = RigIn(dir);
        var vm = new BodyRigViewModel(block);

        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BodyRigViewModel.StoreTargetHint)) raised++;
        };

        vm.NewProfileName = "standing";

        Assert.IsGreaterThan(0, raised, "changing the name must re-announce the hint");
        Assert.AreEqual("Store \u2192 standing.txt", vm.StoreTargetHint);
    }

    // ── Defaults ────────────────────────────────────────────────────────────

    [TestMethod]
    public void FreshDefaults_PreBindEverySegmentToItsOwnSensor()
    {
        // Deliberate, and kept: it is what lets the example pipelines produce a pose with no
        // calibration file at all. It used to also hide the fact that a reopened pipeline was
        // running on defaults — the card read "3 bound" either way. That is no longer the case,
        // because ConfigureInput now applies the stored calibration.
        using var block = new BodyRigBlock();
        block.NumOfChannels = 3;

        for (int i = 0; i < 3; i++)
            Assert.AreEqual(i, block.GetSensorIndex(i), $"segment {i} is pre-bound to sensor {i}");
    }
}
