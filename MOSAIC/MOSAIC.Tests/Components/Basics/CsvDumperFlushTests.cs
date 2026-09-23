using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;

namespace MOSAIC.Tests.Components.Basics;

/// <summary>
/// Tests that recorded data actually reaches disk.
/// </summary>
/// <remarks>
/// <para>
/// The writer flushes on an interval that defaults to 10 seconds, and the interval check used to
/// sit <em>inside</em> the loop that drains queued rows — so a flush could only ever happen as a
/// side effect of the next row arriving. A stream that went quiet (capture finished, rig switched
/// off, battery flat) left everything since the last flush sitting in the StreamWriter and the
/// 64 KB FileStream buffer. A capture shorter than the flush interval produced a zero-byte file.
/// </para>
/// <para>
/// That is the failure mode that matters for teaching: a student records thirty seconds, closes
/// the app, and finds an empty CSV with no error anywhere.
/// </para>
/// </remarks>
[TestClass]
public class CsvDumperFlushTests
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
        var dir = Path.Combine(Path.GetTempPath(), "mosaic-csv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Reads the file while the dumper still holds it open (FileShare.Read).</summary>
    private static string[] ReadWhileOpen(string path)
    {
        if (!File.Exists(path)) return [];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [TestMethod]
    public async Task PeriodicFlush_WritesAQuietStreamWithoutAnotherSample()
    {
        var dir = NewTempDir();
        await using var dumper = new CsvDumper(dir, "quiet", TimeSpan.FromMilliseconds(50));
        dumper.Enqueue(0, 42d);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ReadWhileOpen(Path.Combine(dir, "quiet.csv")).Length == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.HasCount(1, ReadWhileOpen(Path.Combine(dir, "quiet.csv")));
    }

    [TestMethod]
    public async Task FlushAsync_ActuallyPutsRowsOnDisk()
    {
        // FlushAsync used to be a stub whose whole body was `await Task.Delay(1)` — it named an
        // intent it never carried out, so callers believed their data was saved when it was not.
        var dir = NewTempDir();
        var dumper = new CsvDumper(dir, "shortcapture", TimeSpan.FromSeconds(30));

        for (int i = 0; i < 25; i++)
            dumper.Enqueue(i * 0.01, new[] { (double)i, i + 0.5 });

        await dumper.FlushAsync();

        var lines = ReadWhileOpen(Path.Combine(dir, "shortcapture.csv"));
        Assert.HasCount(25, lines, "every enqueued row must be on disk after an explicit flush");

        await dumper.DisposeAsync();
    }

    [TestMethod]
    public async Task AShortCapture_DoesNotProduceAnEmptyFile()
    {
        // The lab scenario: a capture far shorter than the flush interval.
        var dir = NewTempDir();
        var dumper = new CsvDumper(dir, "labrun", TimeSpan.FromSeconds(60));

        dumper.Enqueue(0.0, new[] { 1.0, 2.0, 3.0 });
        dumper.Enqueue(0.01, new[] { 4.0, 5.0, 6.0 });

        await dumper.FlushAsync();

        var path = Path.Combine(dir, "labrun.csv");
        Assert.IsTrue(File.Exists(path));
        Assert.IsGreaterThan(0L, new FileInfo(path).Length, "a short capture must not be a 0-byte file");

        await dumper.DisposeAsync();
    }

    [TestMethod]
    public async Task FlushAsync_WorksWhenTheWriterLoopIsAlreadyParked()
    {
        // THE case a flush exists for, and the one a flag-based implementation cannot serve:
        // the rows have already been drained into the StreamWriter and the loop is parked on
        // WaitToReadAsync. Nothing further will arrive to wake it, so the flush request must
        // itself be the thing that does.
        var dir = NewTempDir();
        var dumper = new CsvDumper(dir, "parked", TimeSpan.FromMinutes(5));

        for (int i = 0; i < 10; i++) dumper.Enqueue(i, (double)i);

        // Let the writer loop consume the queue and go back to sleep.
        await Task.Delay(250);

        await dumper.FlushAsync();

        Assert.HasCount(10, ReadWhileOpen(Path.Combine(dir, "parked.csv")),
            "a parked writer loop must still honour a flush request");

        await dumper.DisposeAsync();
    }

    [TestMethod]
    public async Task FlushAsync_ReturnsWithoutAFixedDelay()
    {
        // This is a real-time capture path: the flush is signalled through the queue and
        // completes when the write completes. It must not sit on a polling interval.
        var dir = NewTempDir();
        var dumper = new CsvDumper(dir, "prompt", TimeSpan.FromMinutes(5));

        for (int i = 0; i < 50; i++) dumper.Enqueue(i, (double)i);
        await Task.Delay(200);   // ensure the loop is parked, worst case for a polled design

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await dumper.FlushAsync();
        sw.Stop();

        Assert.HasCount(50, ReadWhileOpen(Path.Combine(dir, "prompt.csv")));
        Assert.IsLessThan(400L, sw.ElapsedMilliseconds,
            $"flush took {sw.ElapsedMilliseconds} ms — it should be signal-driven, not polled");

        await dumper.DisposeAsync();
    }

    [TestMethod]
    public async Task FlushAsync_AfterDispose_DoesNotHang()
    {
        var dir = NewTempDir();
        var dumper = new CsvDumper(dir, "disposed", TimeSpan.FromMinutes(5));
        dumper.Enqueue(0, 1.0);
        await dumper.DisposeAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await dumper.FlushAsync();
        sw.Stop();

        Assert.IsLessThan(1000L, sw.ElapsedMilliseconds, "must not wait on a completed writer");
    }

    [TestMethod]
    public async Task FlushAsync_OrdersAfterEveryRowQueuedBeforeIt()
    {
        // The request travels the same ordered queue as the data, so everything enqueued
        // beforehand is guaranteed to be on disk when it completes.
        var dir = NewTempDir();
        var dumper = new CsvDumper(dir, "ordered", TimeSpan.FromMinutes(5));

        for (int i = 0; i < 500; i++) dumper.Enqueue(i, (double)i);
        await dumper.FlushAsync();

        Assert.HasCount(500, ReadWhileOpen(Path.Combine(dir, "ordered.csv")));

        await dumper.DisposeAsync();
    }

    [TestMethod]
    public async Task DisposeStillFlushesEverything()
    {
        var dir = NewTempDir();
        var dumper = new CsvDumper(dir, "onstop", TimeSpan.FromSeconds(60));

        for (int i = 0; i < 100; i++) dumper.Enqueue(i, (double)i);

        await dumper.DisposeAsync();

        Assert.HasCount(100, ReadWhileOpen(Path.Combine(dir, "onstop.csv")));
    }

    [TestMethod]
    public async Task RowsAreInvariantCulture_SoPandasCanReadThem()
    {
        // A decimal-comma locale would produce "0,5" and silently split one column into two.
        var dir = NewTempDir();
        var dumper = new CsvDumper(dir, "culture", TimeSpan.FromSeconds(60));

        dumper.Enqueue(1.5, new[] { 0.5, -0.25 });
        await dumper.FlushAsync();

        var lines = ReadWhileOpen(Path.Combine(dir, "culture.csv"));
        Assert.HasCount(1, lines);
        Assert.Contains("0.5", lines[0]);
        Assert.DoesNotContain("0,5", lines[0]);
        Assert.HasCount(3, lines[0].Trim().Split(','), "timestamp + two values");

        await dumper.DisposeAsync();
    }

    [TestMethod]
    public async Task TimestampIsTheFirstColumn()
    {
        // Students align two sensors on this column, so its position is a contract.
        var dir = NewTempDir();
        var dumper = new CsvDumper(dir, "stamp", TimeSpan.FromSeconds(60));

        dumper.Enqueue(1234.5, new[] { 7.0 });
        await dumper.FlushAsync();

        var cells = ReadWhileOpen(Path.Combine(dir, "stamp.csv"))[0].Trim().Split(',');
        Assert.AreEqual(1234.5, double.Parse(cells[0], CultureInfo.InvariantCulture), 1e-9);
        Assert.AreEqual(7.0, double.Parse(cells[1], CultureInfo.InvariantCulture), 1e-9);

        await dumper.DisposeAsync();
    }

    [TestMethod]
    public async Task NoHeaderRowIsWritten_WhichPythonMustBeToldAbout()
    {
        // Documents the contract rather than changing it: pandas defaults to header=0 and would
        // eat the first sample, labelling the columns with its values.
        var dir = NewTempDir();
        var dumper = new CsvDumper(dir, "noheader", TimeSpan.FromSeconds(60));

        dumper.Enqueue(0.0, new[] { 1.0, 2.0 });
        await dumper.FlushAsync();

        var first = ReadWhileOpen(Path.Combine(dir, "noheader.csv"))[0];
        Assert.IsTrue(double.TryParse(first.Split(',')[0], NumberStyles.Float,
                                      CultureInfo.InvariantCulture, out _),
            "the first line is data, not a header — handouts must say pd.read_csv(..., header=None)");

        await dumper.DisposeAsync();
    }
}
