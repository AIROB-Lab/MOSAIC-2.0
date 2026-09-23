using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;

namespace MOSAIC.Tests.Components.Basics;

/// <summary>
/// Behaviour of the per-block recording switch on <see cref="BaseBlock"/>.
///
/// The pipeline builder sets no paths: a block carries a boolean, and the folder comes from the
/// shared <see cref="IRecordingDestination"/> that the block factory attaches. What these tests pin
/// down is the contract that makes that safe to expose as a toggle in the UI:
///   * Switching on writes to the shared session folder under the block's own name.
///   * The switch works while the pipeline runs — on opens a file, off flushes and closes it.
///   * Switching on with no destination configured leaves the switch OFF, so a block never claims
///     to be recording while nothing reaches disk.
///   * A dumper opened from an explicit <c>Path</c> is reflected by the switch rather than
///     contradicted by it, and keeps writing to that same file across an off/on cycle.
///
/// Rows are written by a background task, so every assertion about file contents goes through
/// <see cref="StopAndReadValues"/>, which closes the recording (the dumper's hard flush guarantee)
/// before reading.
/// </summary>
[TestClass]
public class BlockRecordingTests
{
    /// <summary>Smallest possible block: republishes whatever it receives.</summary>
    private sealed class EchoBlock : BaseBlock
    {
        private readonly string? _dumpPrefix;

        public EchoBlock(string name, string? dumpPrefix = null) : base(name) => _dumpPrefix = dumpPrefix;

        protected override void OnReceive(object sender, object value) => Publish(value);

        /// <summary>Stands in for a block that overrides its recording file name.</summary>
        protected override string DumpFilePrefix => _dumpPrefix ?? base.DumpFilePrefix;

        /// <summary>
        /// Stands in for the <c>InitDumper</c> the factory runs when the config names a
        /// <c>Path</c> — the state <see cref="BaseBlock.AttachRecording"/> has to detect.
        /// </summary>
        /// <remarks>
        /// Goes through <c>InitDumper</c> rather than assigning the dumper, because that call is also
        /// what records the per-block destination the toggle and the JSON export rely on.
        /// </remarks>
        public void OpenDumperFromPath(string folder)
            => InitDumper(EmptyServices.Instance, folder);
    }

    /// <summary>Stands in for a block whose <c>Path</c> addresses something other than a CSV folder.</summary>
    private sealed class ModelPathBlock(string name) : BaseBlock(name)
    {
        protected override void OnReceive(object sender, object value) => Publish(value);

        protected override bool PathIsDumpFolder => false;

        public void OpenDumperFromPath(string folder) => InitDumper(EmptyServices.Instance, folder);
    }

    /// <summary>
    /// <c>CsvDumper.ConfigureInput</c> takes a provider only to reach ActivatorUtilities; the dumper
    /// itself resolves nothing, so an empty provider is enough here.
    /// </summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Destination pointing at a temp folder, or at nothing when <c>root</c> is null.</summary>
    private sealed class FakeDestination(string? root) : IRecordingDestination
    {
        public bool IsConfigured => root is not null;

        public string? SessionFolder
        {
            get
            {
                if (root is null) return null;
                Directory.CreateDirectory(root);
                return root;
            }
        }
    }

    private string _tempRoot = "";

    [TestInitialize]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mosaic_rec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Closes the recording so the writer flushes, then returns the value column of each CSV row.
    /// </summary>
    /// <param name="expectedRows">
    /// How many rows the caller is about to assert on. The poll waits for that many rather than
    /// for the file merely to be non-empty.
    /// </param>
    /// <remarks>
    /// The close is fire-and-forget, so the file is polled briefly rather than read immediately.
    /// Waiting for a row count is what makes the poll correct across an off/on cycle: the first
    /// dumper's close has already flushed its rows into the file the second one appends to, so
    /// "not empty" is true long before the rows under test arrive - which is exactly when the
    /// parallel test runner tends to read it.
    /// Only the value column is compared: the timestamp column is wall-clock and could coincidentally
    /// contain any digits the test is looking for.
    /// </remarks>
    private static double[] StopAndReadValues(EchoBlock block, string folder, int expectedRows)
    {
        block.IsRecording = false;

        var file = Path.Combine(folder, $"{block.Name}.csv");
        double[] values = [];

        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(file))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd();

                    if (text.Length > 0)
                    {
                        values = text
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => double.Parse(
                                line.Trim().Split(',')[1], CultureInfo.InvariantCulture))
                            .ToArray();

                        if (values.Length >= expectedRows) return values;
                    }
                }
                catch (IOException) { /* writer still closing the handle */ }
            }

            Thread.Sleep(20);
        }

        return values;
    }

    /// <summary>Reads the value column of an already-closed recording.</summary>
    /// <param name="expectedRows">
    /// How many rows the caller is about to assert on; the poll waits for that many, for the same
    /// reason as in <see cref="StopAndReadValues"/>.
    /// </param>
    private static double[] ReadValues(string folder, string fileName, int expectedRows)
    {
        var file = Path.Combine(folder, $"{fileName}.csv");
        double[] values = [];

        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(file))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd();

                    if (text.Length > 0)
                    {
                        values = text
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => double.Parse(
                                line.Trim().Split(',')[1], CultureInfo.InvariantCulture))
                            .ToArray();

                        if (values.Length >= expectedRows) return values;
                    }
                }
                catch (IOException) { /* writer still closing the handle */ }
            }

            Thread.Sleep(20);
        }

        return values;
    }

    // ------------------------------------------------------------------ config-driven start

    [TestMethod]
    public void SwitchedOn_RecordsUnderTheBlockName()
    {
        using var block = new EchoBlock("Rectifier");
        block.AttachRecording(new FakeDestination(_tempRoot));

        block.IsRecording = true;
        Assert.IsTrue(block.IsRecording);

        block.ReceiveInput(block, 1.0);
        block.ReceiveInput(block, 2.0);

        // Named after the block, in the session folder — no path was ever configured on the block.
        CollectionAssert.AreEqual(new[] { 1.0, 2.0 }, StopAndReadValues(block, _tempRoot, expectedRows: 2));
    }

    [TestMethod]
    public void AttachRecording_ByItself_LeavesRecordingOff()
    {
        using var block = new EchoBlock("Quiet");

        block.AttachRecording(new FakeDestination(_tempRoot));

        Assert.IsFalse(block.IsRecording);
        Assert.IsFalse(File.Exists(Path.Combine(_tempRoot, "Quiet.csv")));
    }

    // ------------------------------------------------------------------ live toggling

    [TestMethod]
    public void IsRecording_ToggledOnAtRuntime_WritesFromThatPointOn()
    {
        using var block = new EchoBlock("Late");
        block.AttachRecording(new FakeDestination(_tempRoot));

        block.ReceiveInput(block, 42.0);   // before the switch — must not be logged

        block.IsRecording = true;
        Assert.IsTrue(block.IsRecording);

        block.ReceiveInput(block, 7.0);

        CollectionAssert.AreEqual(new[] { 7.0 }, StopAndReadValues(block, _tempRoot, expectedRows: 1));
    }

    [TestMethod]
    public void IsRecording_ToggledOff_StopsWritingAndResumesIntoTheSameFile()
    {
        using var block = new EchoBlock("Cycled");
        block.AttachRecording(new FakeDestination(_tempRoot));

        block.IsRecording = true;
        block.ReceiveInput(block, 1.0);

        block.IsRecording = false;
        Assert.IsFalse(block.IsRecording);
        block.ReceiveInput(block, 2.0);     // dropped: nothing is open

        block.IsRecording = true;
        Assert.IsTrue(block.IsRecording);
        block.ReceiveInput(block, 3.0);

        // One file per block per session — re-opening appends rather than starting a second one.
        CollectionAssert.AreEqual(new[] { 1.0, 3.0 }, StopAndReadValues(block, _tempRoot, expectedRows: 2));
        Assert.AreEqual(1, Directory.GetFiles(_tempRoot, "Cycled*.csv").Length);
    }

    // ------------------------------------------------------------------ no destination

    [TestMethod]
    public void IsRecording_WithNoDestination_StaysOff()
    {
        using var block = new EchoBlock("Homeless");
        block.AttachRecording(new FakeDestination(null));

        Assert.IsFalse(block.CanRecord);

        block.IsRecording = true;

        // The switch must reject rather than latch — a ticked box with no file is the worst outcome.
        Assert.IsFalse(block.IsRecording);
    }

    [TestMethod]
    public void AttachRecording_NullDestination_LeavesBlockUsable()
    {
        // Blocks built outside the factory get no destination at all.
        using var block = new EchoBlock("Standalone");

        block.AttachRecording(null);
        block.IsRecording = true;

        Assert.IsFalse(block.CanRecord);
        Assert.IsFalse(block.IsRecording);
    }

    // ------------------------------------------------------------------ legacy explicit Path

    [TestMethod]
    public void AttachRecording_DumperAlreadyOpen_ReportsRecording()
    {
        using var block = new EchoBlock("Legacy");
        block.OpenDumperFromPath(_tempRoot);

        block.AttachRecording(new FakeDestination(_tempRoot));

        Assert.IsTrue(block.IsRecording, "A block already writing must show as recording.");

        block.ReceiveInput(block, 5.0);
        CollectionAssert.AreEqual(new[] { 5.0 }, StopAndReadValues(block, _tempRoot, expectedRows: 1));
    }

    [TestMethod]
    public void ExplicitPath_SurvivesAnOffOnCycle()
    {
        // The bug this pins: without a remembered destination, resuming a Path-configured recording
        // restarted it in the shared session folder, splitting one block's session across two places.
        var sessionFolder = Path.Combine(_tempRoot, "session");
        var explicitFolder = Path.Combine(_tempRoot, "explicit");
        Directory.CreateDirectory(explicitFolder);

        using var block = new EchoBlock("Pinned", "Pinned_out");
        block.OpenDumperFromPath(explicitFolder);
        block.AttachRecording(new FakeDestination(sessionFolder));

        block.ReceiveInput(block, 1.0);
        block.IsRecording = false;
        block.IsRecording = true;
        block.ReceiveInput(block, 2.0);
        block.IsRecording = false;

        // Same folder, same file name, both values — and nothing in the shared session folder.
        CollectionAssert.AreEqual(new[] { 1.0, 2.0 }, ReadValues(explicitFolder, "Pinned_out", expectedRows: 2));
        Assert.IsFalse(Directory.Exists(sessionFolder), "A per-block Path must not fall back to the session folder.");
    }

    [TestMethod]
    public void ExplicitPath_RoundTripsThroughExport()
    {
        var explicitFolder = Path.Combine(_tempRoot, "explicit");
        Directory.CreateDirectory(explicitFolder);

        using var block = new EchoBlock("Saved");
        block.OpenDumperFromPath(explicitFolder);
        block.AttachRecording(new FakeDestination(_tempRoot));

        // Saving must not quietly move the block to the shared folder on the next load.
        Assert.AreEqual(explicitFolder, block.ToJsonModel().Path);
    }

    [TestMethod]
    public void ExplicitPathSwitchedOff_StillExportsThePath()
    {
        var explicitFolder = Path.Combine(_tempRoot, "explicit");
        Directory.CreateDirectory(explicitFolder);

        using var block = new EchoBlock("SavedOff");
        block.OpenDumperFromPath(explicitFolder);
        block.AttachRecording(new FakeDestination(_tempRoot));

        block.IsRecording = false;

        // A Path in the config means the block records there. Switching it off is a runtime action,
        // and the config keeps saying Path, so reloading the file arms the block again.
        Assert.AreEqual(explicitFolder, block.ToJsonModel().Path);
    }

    [TestMethod]
    public void DumpFilePrefix_NamesTheFileForBothWaysRecordingStarts()
    {
        // The bug this pins: the file name used to come from an InitDumper argument when a Path
        // opened the dumper, and from DumpFilePrefix when the toggle did, so one block wrote to two
        // different names depending on how it started.
        var explicitFolder = Path.Combine(_tempRoot, "explicit");
        Directory.CreateDirectory(explicitFolder);

        using var viaPath = new EchoBlock("Named", "Named_out");
        viaPath.OpenDumperFromPath(explicitFolder);
        viaPath.AttachRecording(new FakeDestination(_tempRoot));
        viaPath.ReceiveInput(viaPath, 1.0);
        viaPath.IsRecording = false;

        using var viaToggle = new EchoBlock("Named", "Named_out");
        viaToggle.AttachRecording(new FakeDestination(_tempRoot));
        viaToggle.IsRecording = true;
        viaToggle.ReceiveInput(viaToggle, 2.0);
        viaToggle.IsRecording = false;

        CollectionAssert.AreEqual(new[] { 1.0 }, ReadValues(explicitFolder, "Named_out", expectedRows: 1));
        CollectionAssert.AreEqual(new[] { 2.0 }, ReadValues(_tempRoot, "Named_out", expectedRows: 1));
    }

    [TestMethod]
    public void PathIsDumpFolderFalse_IgnoresThePathEntirely()
    {
        // Path means a model directory for these, so the factory's InitDumper must not drop a CSV
        // in among the saved model files.
        var modelFolder = Path.Combine(_tempRoot, "model");
        Directory.CreateDirectory(modelFolder);

        using var block = new ModelPathBlock("Predictor");
        block.OpenDumperFromPath(modelFolder);
        block.AttachRecording(new FakeDestination(_tempRoot));

        Assert.IsFalse(block.IsRecording, "A block that declines Path must not come up recording.");

        block.ReceiveInput(block, 1.0);
        Thread.Sleep(100);

        Assert.AreEqual(0, Directory.GetFiles(modelFolder).Length, "Nothing may be written to a model directory.");
        Assert.IsNull(block.ToJsonModel().Path, "A declined Path is not a per-block dump folder.");
    }

    [TestMethod]
    public void SharedFolderRecording_AddsNothingToTheConfig()
    {
        using var block = new EchoBlock("Shared");
        block.AttachRecording(new FakeDestination(_tempRoot));
        block.IsRecording = true;

        // A block on the shared folder is a session setting only: nothing about it reaches the JSON.
        Assert.IsNull(block.ToJsonModel().Path);
    }

}
