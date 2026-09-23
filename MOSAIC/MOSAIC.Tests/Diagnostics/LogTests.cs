using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Diagnostics;

namespace MOSAIC.Tests.Diagnostics;

/// <summary>
/// Guards the properties of <see cref="Log"/> that a regression would break silently.
/// </summary>
/// <remarks>
/// <para>
/// The logger exists because <c>MOSAIC.Desktop</c> is a Windows GUI-subsystem executable with no
/// console: in the shipped student build a <c>Console.WriteLine</c> writes to a handle that does not
/// exist. So the two things that must never regress are that a call site is free when its level is
/// off — otherwise per-packet logging on an EMG data thread becomes unaffordable and gets deleted
/// again — and that no call can ever throw, because a logger that faults while reporting a fault is
/// worse than no logger at all.
/// </para>
/// <para>
/// <see cref="Log"/> is a static facade over process-wide state (one minimum level, one ring, one
/// file sink), so this class is <see cref="DoNotParallelizeAttribute"/>: MSTest runs the whole
/// assembly method-parallel, and a neighbouring test moving <see cref="Log.MinimumLevel"/> mid-assert
/// would make every test here a coin flip.
/// </para>
/// <para>
/// Determinism comes from two places rather than from sleeps. With no sink open,
/// <c>Log.Write</c> reaches the ring synchronously on the calling thread, so every ring assertion is
/// exact; with a sink open, <see cref="Log.FlushCritical"/> drains the queue and flushes the file on
/// the CALLING thread, so every file assertion is exact too. Nothing here waits on the writer task.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public class LogTests
{
    /// <summary>Category every entry these tests produce carries, so foreign entries can be filtered out.</summary>
    private const string Category = "LogTests";

    private readonly List<string> _tempDirectories = [];
    private readonly List<string> _tempFiles = [];
    private LogLevel _savedLevel;

    [TestInitialize]
    public void Setup()
    {
        _savedLevel = Log.MinimumLevel;

        // No sink: entries then travel to the ring on this thread, which is what makes the ring
        // assertions below exact rather than a race with the writer task.
        Log.Shutdown();
        Log.MinimumLevel = LogLevel.Debug;
        Log.Ring.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Closes the file handles before the directories are removed.
        Log.Shutdown();
        Log.MinimumLevel = _savedLevel;
        Log.Ring.Clear();

        foreach (var directory in _tempDirectories)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- level gating

    /// <summary>
    /// The single most important test in this file: it is the whole performance argument for the
    /// interpolated-string handlers.
    /// </summary>
    /// <remarks>
    /// Every hole reads <see cref="EvaluationProbe.Value"/>, which counts its own evaluations. Because
    /// each handler's constructor has an <c>out bool shouldAppend</c>, Roslyn puts the
    /// <c>AppendFormatted</c> calls — and therefore the argument expressions — inside an <c>if</c>.
    /// A disabled level must not read the property even once. If the handler overloads were ever lost
    /// (deleted, or shadowed by a plain <c>string</c> overload winning resolution) the interpolated
    /// string would be built eagerly at every call site on every data thread, and this counter is the
    /// only thing that would notice.
    /// </remarks>
    [TestMethod]
    public void ADisabledLevel_NeverEvaluatesItsInterpolationHoles()
    {
        Log.MinimumLevel = LogLevel.Error;
        var probe = new EvaluationProbe();

        Log.Debug(Category, $"debug {probe.Value}");
        Log.Info(Category, $"info {probe.Value}");
        Log.Warn(Category, $"warn {probe.Value}");
        Log.Debug(Category, "rig1", $"debug {probe.Value}");
        Log.Info(Category, "rig1", $"info {probe.Value}");
        Log.Warn(Category, "rig1", $"warn {probe.Value}");

        Assert.AreEqual(0, probe.Evaluations,
            "a switched-off level must not evaluate its interpolation holes — that is the whole point of the handlers");
        Assert.HasCount(0, Mine(), "a switched-off level must not produce an entry either");

        // Positive control: without this the test would also pass if the probe simply never worked.
        Log.Error(Category, $"error {probe.Value}");
        Assert.AreEqual(1, probe.Evaluations, "an enabled level must evaluate its holes exactly once");
        Assert.HasCount(1, Mine());
    }

    [TestMethod]
    public void MinimumLevelNone_SwitchesEveryLevelOff()
    {
        Log.MinimumLevel = LogLevel.None;
        var probe = new EvaluationProbe();

        Log.Error(Category, $"error {probe.Value}");
        Log.Error(Category, "plain error");

        Assert.IsFalse(Log.IsEnabled(LogLevel.Debug));
        Assert.IsFalse(Log.IsEnabled(LogLevel.Error), "None means off, including the loudest level");
        Assert.AreEqual(0, probe.Evaluations);
        Assert.HasCount(0, Mine());
    }

    [TestMethod]
    public void IsEnabled_FollowsTheConfiguredMinimum()
    {
        Log.MinimumLevel = LogLevel.Warn;

        Assert.IsFalse(Log.IsEnabled(LogLevel.Debug));
        Assert.IsFalse(Log.IsEnabled(LogLevel.Info));
        Assert.IsTrue(Log.IsEnabled(LogLevel.Warn));
        Assert.IsTrue(Log.IsEnabled(LogLevel.Error));
    }

    [TestMethod]
    public void MinimumLevel_IsClampedToTheValidRange()
    {
        // No UI writes this: the capture level is startup state, and its one external writer is the
        // MOSAIC_LOG_LEVEL variable, which Enum.TryParse will happily turn into any number at all.
        // An out-of-range value must not be able to turn the gate into something that lets nothing —
        // or everything — through.
        Log.MinimumLevel = (LogLevel)(-7);
        Assert.AreEqual(LogLevel.Debug, Log.MinimumLevel);

        Log.MinimumLevel = (LogLevel)42;
        Assert.AreEqual(LogLevel.None, Log.MinimumLevel);
    }

    // ---------------------------------------------------------------- never throws

    [TestMethod]
    public void Logging_BeforeInitialize_DoesNotThrowAndStillReachesTheRing()
    {
        // Setup shut the logger down, so this is the uninitialised state: no directory, no sink.
        MustNotThrow("Log.Info with no sink", () => Log.Info(Category, "no file yet"));
        MustNotThrow("Log.Debug with an interpolated message", () => Log.Debug(Category, $"still {1} entry"));
        MustNotThrow("Log.Error with an exception", () => Log.Error(Category, new InvalidOperationException("boom"), "while starting"));

        Assert.HasCount(3, Mine(), "with no file the ring is the whole logger, so the panel still works");
    }

    [TestMethod]
    public void Initialize_WithAnUnusableDirectory_FallsBackToTheRingWithoutThrowing()
    {
        // A plain file where a folder is wanted: Directory.CreateDirectory cannot succeed, which is
        // the same shape as a read-only or missing %AppData% on a locked-down lab machine.
        var blocker = NewTempFile();

        MustNotThrow("Log.Initialize on an unusable path", () => Log.Initialize(blocker));

        Assert.IsNull(Log.LogDirectory, "an unusable folder must leave the logger file-less, not half-open");
        Assert.IsNull(Log.LogFilePath);

        Log.MinimumLevel = LogLevel.Debug;   // set after Initialize: it re-reads MOSAIC_LOG_LEVEL
        Log.Ring.Clear();

        MustNotThrow("logging with a broken sink", () => Log.Warn(Category, "the rig still runs"));
        Assert.HasCount(1, Mine(), "a broken file must degrade to the in-memory ring, not silence the logger");
    }

    [TestMethod]
    public void ShutdownAndFlush_AreSafeWithNoSinkAndAreIdempotent()
    {
        MustNotThrow("Shutdown twice over", () => { Log.Shutdown(); Log.Shutdown(); });
        MustNotThrow("FlushCritical with no sink", () => Log.FlushCritical(TimeSpan.FromMilliseconds(50)));
        MustNotThrow("FlushCritical with a negative timeout", () => Log.FlushCritical(TimeSpan.FromSeconds(-1)));
        MustNotThrow("logging after shutdown", () => Log.Error(Category, "after shutdown"));

        Assert.HasCount(1, Mine(), "an entry logged after shutdown must still reach the panel");
    }

    /// <summary>
    /// A failure inside the logger must be counted, not swallowed. The panel reads these two members
    /// to tell the student that the thing recording their session has itself stopped working.
    /// </summary>
    [TestMethod]
    public void AnUnusableDirectory_IsCountedRatherThanFailingSilently()
    {
        var before = Log.InternalFailureCount;

        Log.Initialize(NewTempFile());

        Assert.IsGreaterThan(before, Log.InternalFailureCount,
            "a folder that cannot be created must raise the internal-failure count");
        Assert.IsNotNull(Log.LastInternalError,
            "the failure must also leave a description, otherwise the panel has nothing to show");
    }

    // ---------------------------------------------------------------- entry contents

    [TestMethod]
    public void LevelCategoryAndInstance_SurviveIntoTheEntry()
    {
        Log.Warn(Category, "rig7", "throttle engaged");

        var entry = Mine().Single();
        Assert.AreEqual(LogLevel.Warn, entry.Level);
        Assert.AreEqual(Category, entry.Category);
        Assert.AreEqual("rig7", entry.Instance);
        Assert.AreEqual("throttle engaged", entry.Message);
        Assert.AreEqual("[LogTests 'rig7']", entry.Prefix);

        var line = entry.ToLogLine();
        Assert.Contains("WARN", line);
        Assert.Contains("[LogTests 'rig7'] throttle engaged", line);
    }

    [TestMethod]
    public void AnEmptyInstance_IsNormalisedAway()
    {
        // Call sites pass Name straight through, and an unnamed block has an empty one. "[LogTests '']"
        // would be noise in every line of the file.
        Log.Info(Category, string.Empty, "pipeline started");

        var entry = Mine().Single();
        Assert.IsNull(entry.Instance);
        Assert.AreEqual("[LogTests]", entry.Prefix);
    }

    [TestMethod]
    public void AnInterpolatedMessage_KeepsItsHoleValues()
    {
        var channels = 3;
        var port = "COM4";
        Log.Info(Category, $"opened {channels} channels on {port}");

        Assert.AreEqual("opened 3 channels on COM4", Mine().Single().Message);
    }

    [TestMethod]
    public void ErrorWithAnException_RecordsItAndKeepsOneRecordPerLine()
    {
        Exception caught = new InvalidOperationException("placeholder");
        try { throw new InvalidOperationException("rig not armed"); }
        catch (Exception ex) { caught = ex; }

        Log.Error(Category, caught, "starting the pipeline");

        var entry = Mine().Single();
        Assert.AreEqual(LogLevel.Error, entry.Level);
        Assert.Contains("InvalidOperationException", entry.Message);
        Assert.Contains("rig not armed", entry.Message);

        // A stack trace spans lines, and a flush-left continuation would read as its own record to
        // Select-String or grep — which is how a bug report gets skimmed.
        var continuations = entry.ToLogLine().Split('\n').Skip(1).ToList();

        // Asserted before the loop, which would otherwise iterate an empty sequence and so still pass
        // if the exception detail had been dropped from the line altogether.
        Assert.IsGreaterThan(0, continuations.Count,
            "the line must carry the exception detail, not just the message it was logged with");

        foreach (var continuation in continuations)
        {
            Assert.IsTrue(continuation.StartsWith("    ", StringComparison.Ordinal),
                "a continuation line must stay indented under the record it belongs to");
        }
    }

    // ---------------------------------------------------------------- bounded memory

    /// <summary>
    /// The ring is what bounds the logger's memory over a long lab session, no matter how loud a
    /// device gets. It must overwrite the OLDEST entries: the newest are the ones describing whatever
    /// just went wrong.
    /// </summary>
    [TestMethod]
    public void Ring_KeepsAtMostItsCapacity_AndDropsTheOldestFirst()
    {
        Log.MinimumLevel = LogLevel.Info;

        var capacity = Log.Ring.Capacity;
        var total = capacity + 500;

        Log.Ring.Clear();
        for (var i = 0; i < total; i++) Log.Info(Category, "ring-" + i);

        var retained = Retained().Count;
        Assert.IsTrue(retained <= capacity,
            $"the ring held {retained} entries for a capacity of {capacity} — memory is no longer bounded");

        var mine = Mine().Select(e => e.Message).ToList();
        Assert.IsGreaterThan(0, mine.Count);

        // What survived must be the newest contiguous run — i.e. the oldest are exactly what went.
        for (var i = 0; i < mine.Count; i++)
        {
            Assert.AreEqual("ring-" + (total - mine.Count + i), mine[i],
                "the ring must retain the newest contiguous run of entries");
        }

        Assert.DoesNotContain("ring-0", mine, "the oldest entry must have been overwritten");
        Assert.Contains("ring-" + (total - 1), mine, "the newest entry must always survive");
    }

    [TestMethod]
    public void RingCopyFrom_ResumesFromTheCursorItHandedBack()
    {
        // The panel polls at 30 fps and must not re-render what it already drew.
        Log.Ring.Clear();
        Log.Info(Category, "first");
        Log.Info(Category, "second");

        var firstPass = new List<LogEntry>();
        var cursor = Log.Ring.CopyFrom(0, firstPass, 100);
        Assert.HasCount(2, firstPass.Where(e => e.Category == Category).ToList());

        var secondPass = new List<LogEntry>();
        Assert.AreEqual(cursor, Log.Ring.CopyFrom(cursor, secondPass, 100),
            "an unchanged ring must hand back the same cursor");
        Assert.HasCount(0, secondPass, "nothing new must be replayed");

        Log.Info(Category, "third");
        Log.Ring.CopyFrom(cursor, secondPass, 100);

        var fresh = secondPass.Where(e => e.Category == Category).ToList();
        Assert.HasCount(1, fresh, "only what was added after the cursor may come back");
        Assert.AreEqual("third", fresh[0].Message);
    }

    // ---------------------------------------------------------------- the file

    [TestMethod]
    public void FileSink_PutsTheEntryOnDisk()
    {
        var directory = NewTempDirectory();
        Log.Initialize(directory);
        Log.MinimumLevel = LogLevel.Debug;

        var path = Log.LogFilePath;
        Assert.IsNotNull(path, "a writable folder must yield an open file");
        Assert.AreEqual(directory, Log.LogDirectory);

        var marker = "marker-" + Guid.NewGuid().ToString("N");
        Log.Warn(Category, "rig7", marker);

        // Drains and flushes on this thread, so there is nothing to wait for.
        Log.FlushCritical(TimeSpan.FromSeconds(10));

        var text = ReadWhileOpen(path);
        Assert.Contains(marker, text);
        Assert.Contains("WARN", text);
        Assert.Contains("[LogTests 'rig7']", text);
    }

    [TestMethod]
    public void Initialize_WithADirectoryOverride_ReopensEvenWhenAlreadyRunning()
    {
        // This is the test seam. Without the re-open, a test would silently write into the
        // developer's real %AppData%\MOSAIC\logs whenever the app had already initialised.
        var first = NewTempDirectory();
        var second = NewTempDirectory();

        Log.Initialize(first);
        var firstPath = Log.LogFilePath;

        Log.Initialize(second);
        var secondPath = Log.LogFilePath;

        Assert.IsNotNull(firstPath);
        Assert.IsNotNull(secondPath);
        Assert.AreNotEqual(firstPath, secondPath, "a directory override must re-open the sink");
        Assert.AreEqual(second, Log.LogDirectory);
    }

    /// <summary>
    /// Rotation is what stops one runaway component filling the student's disk. It is driven by bytes
    /// written, so the only honest way to test it is to write that many.
    /// </summary>
    [TestMethod]
    public void FileSink_RollsOverWhenTheFileReachesItsSizeCap()
    {
        var maxBytes = SinkMaxFileBytes();

        var directory = NewTempDirectory();
        Log.Initialize(directory);
        Log.MinimumLevel = LogLevel.Info;

        var firstPath = Log.LogFilePath;
        Assert.IsNotNull(firstPath);

        var chunk = new string('x', 8192);
        var lines = (int)(maxBytes / chunk.Length) + 8;
        for (var i = 0; i < lines; i++) Log.Info(Category, chunk);

        Log.FlushCritical(TimeSpan.FromSeconds(30));

        Assert.AreNotEqual(firstPath, Log.LogFilePath, "the sink must roll over once the cap is reached");

        Log.Shutdown();   // release the handles before measuring

        var files = Directory.GetFiles(directory, "mosaic-*.log");
        Assert.IsGreaterThan(1, files.Length, "rotation must leave the filled file behind, not replace it");

        var rotatedOut = new FileInfo(firstPath).Length;
        Assert.IsTrue(rotatedOut >= maxBytes,
            $"the rotated-out file is {rotatedOut} bytes, below the {maxBytes} byte cap it should have reached");
    }

    // ---------------------------------------------------------------- concurrency

    /// <summary>
    /// Producers are device threads: Delsys notifications, BLE callbacks, UDP receive loops. They log
    /// simultaneously, and none of them may throw or lose an entry doing it.
    /// </summary>
    [TestMethod]
    public void ConcurrentProducers_ThrowNothingAndLoseNothing()
    {
        const int producers = 8;
        const int perProducer = 50;

        Log.Ring.Clear();

        var failures = new ConcurrentQueue<Exception>();
        using var start = new ManualResetEventSlim(false);
        var threads = new Thread[producers];

        for (var t = 0; t < producers; t++)
        {
            var id = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    start.Wait();
                    for (var i = 0; i < perProducer; i++) Log.Debug(Category, $"t{id}-{i}");
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            })
            { IsBackground = true, Name = "log-producer-" + id };

            threads[t].Start();
        }

        start.Set();
        foreach (var thread in threads)
        {
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "a logging thread never finished");
        }

        Assert.AreEqual(0, failures.Count,
            failures.TryPeek(out var first) ? "a producer threw: " + first : "no failure");

        var mine = Mine();
        Assert.HasCount(producers * perProducer, mine, "every concurrent entry must be retained");
        Assert.AreEqual(mine.Count, mine.Select(e => e.Sequence).Distinct().Count(),
            "sequence numbers are the panel's cursor, so two entries must never share one");

        var messages = mine.Select(e => e.Message).ToHashSet();
        for (var t = 0; t < producers; t++)
        {
            for (var i = 0; i < perProducer; i++)
            {
                Assert.Contains($"t{t}-{i}", messages, "an entry went missing under concurrency");
            }
        }
    }

    // ---------------------------------------------------------------- throttling

    [TestMethod]
    public void LogRate_AllowsTheFirstCallAndThenSuppressesWithinTheInterval()
    {
        // Guards the per-call-site throttle that lets a once-per-packet failure be reported without
        // producing one line per packet.
        var rate = new LogRate(TimeSpan.FromHours(1));

        Assert.IsTrue(rate.Allow(), "the first call must always be allowed");
        Assert.IsFalse(rate.Allow());
        Assert.IsFalse(rate.Allow());
        Assert.AreEqual(0, rate.Suppressed, "Suppressed counts what the previous allowed call stood for");

        var open = new LogRate(TimeSpan.Zero);
        Assert.IsTrue(open.Allow());
        Assert.IsTrue(open.Allow(), "a zero interval must not throttle at all");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Reads a log file while the sink still holds it open (it opens FileShare.ReadWrite).</summary>
    private static string ReadWhileOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Everything the ring currently retains, from any category.</summary>
    private static List<LogEntry> Retained()
    {
        var into = new List<LogEntry>();
        Log.Ring.CopyFrom(0, into, int.MaxValue);
        return into;
    }

    /// <summary>
    /// Only the entries these tests produced. The ring is process-wide, so filtering by category keeps
    /// an entry from some other component's background thread out of the assertions.
    /// </summary>
    private static List<LogEntry> Mine() =>
        Retained().Where(e => e.Category == Category).ToList();

    private static void MustNotThrow(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Assert.Fail($"{what} threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The sink's rotation threshold. Read by reflection because <c>LogFileSink</c> is internal, and
    /// falling back to its documented value rather than skipping, so the test keeps running either way.
    /// </summary>
    private static long SinkMaxFileBytes()
    {
        var sink = typeof(Log).Assembly.GetType("MOSAIC.Diagnostics.LogFileSink", throwOnError: false);
        var field = sink?.GetField("MaxFileBytes",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        return field?.GetRawConstantValue() as long? ?? 8L * 1024 * 1024;
    }

    private string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mosaic-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    /// <summary>A plain file, used where a folder is expected so that opening the sink must fail.</summary>
    private string NewTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "mosaic-log-" + Guid.NewGuid().ToString("N") + ".occupied");
        File.WriteAllText(path, "not a directory");
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Counts how often its value was read. Placed in an interpolation hole, it turns "the handler
    /// short-circuited" into something a test can assert.
    /// </summary>
    private sealed class EvaluationProbe
    {
        private int _evaluations;

        public int Evaluations => Volatile.Read(ref _evaluations);

        public int Value
        {
            get
            {
                Interlocked.Increment(ref _evaluations);
                return 1;
            }
        }
    }
}
