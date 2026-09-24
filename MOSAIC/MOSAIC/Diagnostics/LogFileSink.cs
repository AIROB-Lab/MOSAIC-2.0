using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MOSAIC.Diagnostics;

/// <summary>
/// The bounded queue, the single background writer, the rolling file and its retention policy.
/// </summary>
/// <remarks>
/// <para>
/// Owned entirely by <see cref="Log"/>; nothing else constructs one. The split matters: everything
/// a producer pays for lives in <see cref="TryEnqueue"/>, and everything expensive - formatting a
/// line, touching the disk, rotating, deleting old files - happens on one background task.
/// </para>
/// <para>
/// The channel is bounded and drops the OLDEST entry on overflow. Dropping the newest would hide
/// exactly the burst that caused the overflow, and blocking the producer would stall an EMG data
/// thread on disk I/O, which is the one thing this design must never do. Drops are counted and
/// reported into the log itself, so an overflow is visible rather than silent.
/// </para>
/// <para>
/// Shape (channel, one writer task, batched drain, timed flush) is copied from
/// <c>Components/Basics/CsvDumper.cs</c>, which already solves the same problem for recording.
/// </para>
/// </remarks>
internal sealed class LogFileSink : IAsyncDisposable
{
    /// <summary>Entries buffered before the oldest starts being dropped.</summary>
    internal const int QueueCapacity = 4096;

    /// <summary>File size at which the writer rolls over to the next file.</summary>
    internal const long MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>Log files kept in the folder; older ones are deleted when a new run starts.</summary>
    internal const int MaxRetainedFiles = 10;

    /// <summary>Longest a written line may sit unflushed.</summary>
    internal const int FlushIntervalMs = 250;

    private const int DropReportIntervalMs = 1000;
    private const int MaxWriterRestarts = 3;
    private const int WriterRestartDelayMs = 500;

    private static readonly UTF8Encoding FileEncoding = new(false);
    private static readonly int NewLineByteCount = FileEncoding.GetByteCount(Environment.NewLine);

    private readonly Channel<LogEntry> _channel;
    private readonly LogRing _ring;
    private readonly string _directory;
    private readonly string _baseName;
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;

    private FileStream? _stream;
    private StreamWriter? _writer;
    private volatile string? _currentPath;
    private long _bytesWritten;
    private int _rotation;
    private int _unflushed;
    private long _lastFlushTicks;
    private long _dropped;
    private long _reportedDropped;
    private long _lastDropReportTicks;
    private int _disposed;
    private int _writerStopped;

    /// <summary>Opens the first file and starts the writer task. Never throws.</summary>
    /// <param name="directory">Folder to write into. Must already exist.</param>
    internal LogFileSink(string directory)
    {
        _directory = directory;
        _ring = Log.Ring;
        _baseName = "mosaic-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");

        _channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,

                // Without this the writer's continuation could be inlined onto whichever data
                // thread happened to enqueue, which would put file I/O back on the hot path.
                AllowSynchronousContinuations = false
            },
            _ => Interlocked.Increment(ref _dropped));

        Prune(MaxRetainedFiles - 1);
        Open();

        _lastFlushTicks = Environment.TickCount64;
        _lastDropReportTicks = Environment.TickCount64;
        _writerTask = Task.Run(WriterLoop);
    }

    /// <summary>Path of the file currently being appended to, or null if none could be opened.</summary>
    internal string? CurrentPath => _currentPath;

    /// <summary>Entries the queue has dropped because it was full.</summary>
    internal long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// True once the writer task has given up: the queue is no longer being drained, so nothing new
    /// reaches the log file until the next <see cref="Log.Initialize()"/>. The ring and the panel do
    /// keep filling - <see cref="Log"/> treats a stopped writer as a refusal and routes the entry to
    /// the ring instead. Surfaced to the UI as <see cref="Log.WriterStopped"/>.
    /// </summary>
    internal bool WriterStopped => Volatile.Read(ref _writerStopped) != 0;

    /// <summary>
    /// Hands an entry to the writer. One non-blocking channel write and nothing else - no lock, no
    /// allocation, no disk - which is what makes it safe to call from inside a device lock on a
    /// notification thread.
    /// </summary>
    /// <param name="entry">The entry to write.</param>
    /// <returns>False only once the sink has been shut down.</returns>
    internal bool TryEnqueue(in LogEntry entry)
    {
        try
        {
            return _channel.Writer.TryWrite(entry);
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
            return false;
        }
    }

    /// <summary>
    /// Drains the queue and flushes the file on the CALLING thread, giving up after
    /// <paramref name="timeout"/> rather than deadlocking a dying process.
    /// </summary>
    /// <param name="timeout">How long to wait for the writer to release the file.</param>
    /// <remarks>
    /// Deliberately independent of the writer task: when the crash being logged is the writer's own,
    /// the lines describing it must still reach disk.
    /// </remarks>
    internal void FlushCritical(TimeSpan timeout)
    {
        var ms = (long)timeout.TotalMilliseconds;
        var wait = ms < 0 ? 0 : ms > int.MaxValue ? int.MaxValue : (int)ms;

        var taken = false;
        try
        {
            taken = _fileGate.Wait(wait);
            if (!taken) return;

            while (_channel.Reader.TryRead(out var entry)) WriteEntry(entry);
            FlushFile();
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
        finally
        {
            if (taken)
            {
                try { _fileGate.Release(); } catch (Exception ex) { ReportInternalFailure(ex); }
            }
        }
    }

    /// <summary>Stops the writer, drains what is left and closes the file. Never throws.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            _channel.Writer.TryComplete();
            await Task.WhenAny(_writerTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }

        try { _cts.Cancel(); } catch (Exception ex) { ReportInternalFailure(ex); }

        FlushCritical(TimeSpan.FromSeconds(1));

        var taken = false;
        try
        {
            taken = await _fileGate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            // A timed-out gate means the writer thread is still inside the file. Disposing the stream
            // underneath it would turn shutdown - the very path that saves the last lines before a
            // crash - into an ObjectDisposedException, so the handles are left to process exit
            // instead. Leaking them for the few seconds a dying process has left is the cheaper loss.
            if (taken) Close();
            else ReportInternalFailure("file still in use at shutdown; stream left open deliberately");
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
        finally
        {
            if (taken)
            {
                try { _fileGate.Release(); } catch (Exception ex) { ReportInternalFailure(ex); }
            }

            try { _cts.Dispose(); } catch (Exception ex) { ReportInternalFailure(ex); }
        }
    }

    /// <summary>
    /// Runs the drain continuously. Transient faults are retried; a persistent fault leaves
    /// <see cref="WriterStopped"/> set and records a description in <see cref="Log.LastInternalError"/>.
    /// </summary>
    private async Task WriterLoop()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await DrainLoop().ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                // Expected during disposal; DisposeAsync drains and flushes what is left.
                return;
            }
            catch (Exception ex)
            {
                ReportInternalFailure(ex);

                if (attempt >= MaxWriterRestarts || Volatile.Read(ref _disposed) != 0)
                {
                    Volatile.Write(ref _writerStopped, 1);
                    ReportInternalFailure(
                        "writer stopped after " + (attempt + 1) +
                        " failures; nothing further will reach the log file until restart");
                    return;
                }
            }

            try
            {
                await Task.Delay(WriterRestartDelayMs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportInternalFailure(ex);
            }
        }
    }

    private async Task DrainLoop()
    {
        var token = _cts.Token;

        while (true)
        {
            var wait = _channel.Reader.WaitToReadAsync(token);

            // Nothing queued and something written but unflushed: give the batch its flush
            // interval to fill up, then flush anyway, so a quiet app never leaves its last
            // lines sitting in a buffer that a crash would take with it.
            if (!wait.IsCompleted && Volatile.Read(ref _unflushed) > 0)
            {
                var elapsed = Environment.TickCount64 - _lastFlushTicks;
                var remaining = FlushIntervalMs - elapsed;
                if (remaining > 0)
                {
                    var readable = wait.AsTask();

                    // No cancellation token on the delay: a cancelled Task.Delay nobody awaits
                    // surfaces later as an unobserved task exception, which this logger's own
                    // crash hook would then report as a fault.
                    await Task.WhenAny(readable, Task.Delay((int)remaining)).ConfigureAwait(false);
                    if (!readable.IsCompleted) await GuardedFlushAsync().ConfigureAwait(false);
                    if (!await readable.ConfigureAwait(false)) break;
                }
                else
                {
                    await GuardedFlushAsync().ConfigureAwait(false);
                    if (!await wait.ConfigureAwait(false)) break;
                }
            }
            else if (!await wait.ConfigureAwait(false))
            {
                break;
            }

            await _fileGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                while (_channel.Reader.TryRead(out var entry)) WriteEntry(entry);

                ReportDrops();

                if (_unflushed > 0 && Environment.TickCount64 - _lastFlushTicks >= FlushIntervalMs)
                {
                    FlushFile();
                }
            }
            finally
            {
                _fileGate.Release();
            }
        }
    }

    private async Task GuardedFlushAsync()
    {
        await _fileGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            FlushFile();
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>Writes one entry to the ring and the file. Caller holds the file gate.</summary>
    private void WriteEntry(in LogEntry entry)
    {
        _ring.Add(entry);
        WriteLine(entry);
    }

    /// <summary>Writes one entry to the file only. Caller holds the file gate.</summary>
    private void WriteLine(in LogEntry entry)
    {
        var writer = _writer;
        if (writer is null) return;

        try
        {
            var line = entry.ToLogLine();
            writer.WriteLine(line);

            // Bytes, not characters: the app logs marks such as U+2717 and German text, each of
            // which costs more than one byte, so a character count would overshoot the rotation cap.
            _bytesWritten += FileEncoding.GetByteCount(line) + NewLineByteCount;
            Volatile.Write(ref _unflushed, _unflushed + 1);

            if (_bytesWritten >= MaxFileBytes) Rotate();
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
    }

    /// <summary>
    /// Appends entries captured before this sink existed. File only: <see cref="Log"/> has already
    /// put them in the ring, and adding them again would show the panel every line twice.
    /// </summary>
    /// <param name="entries">Buffered entries, oldest first.</param>
    /// <param name="count">How many leading elements of <paramref name="entries"/> to write.</param>
    internal void WriteBacklog(LogEntry[] entries, int count)
    {
        if (entries is null || count <= 0) return;

        var taken = false;
        try
        {
            taken = _fileGate.Wait(TimeSpan.FromSeconds(1));
            if (!taken) return;

            for (var i = 0; i < count && i < entries.Length; i++) WriteLine(entries[i]);
            FlushFile();
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
        finally
        {
            if (taken)
            {
                try { _fileGate.Release(); } catch (Exception ex) { ReportInternalFailure(ex); }
            }
        }
    }

    private void FlushFile()
    {
        try
        {
            _writer?.Flush();
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
        finally
        {
            Volatile.Write(ref _unflushed, 0);
            _lastFlushTicks = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Turns a full queue into a visible line rather than a silent gap. Caller holds the file gate.
    /// </summary>
    private void ReportDrops()
    {
        var total = Interlocked.Read(ref _dropped);
        if (total <= _reportedDropped) return;
        if (Environment.TickCount64 - _lastDropReportTicks < DropReportIntervalMs) return;

        var lost = total - _reportedDropped;
        _reportedDropped = total;
        _lastDropReportTicks = Environment.TickCount64;

        WriteEntry(new LogEntry(
            Log.NextSequence(),
            DateTime.Now,
            LogLevel.Warn,
            "Log",
            null,
            $"Dropped {lost} messages (queue full)"));
    }

    private void Rotate()
    {
        FlushFile();
        Close();
        _rotation++;
        Open();
        Prune(MaxRetainedFiles - 1);
    }

    private void Open()
    {
        try
        {
            var name = _rotation == 0 ? _baseName + ".log" : _baseName + "-" + _rotation + ".log";
            var path = Path.Combine(_directory, name);

            // FileShare.ReadWrite so a student can open the file in an editor while the app runs.
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
            _writer = new StreamWriter(_stream, FileEncoding) { AutoFlush = false };
            _currentPath = path;
            _bytesWritten = 0;
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
            _writer = null;
            _stream = null;
            _currentPath = null;
        }
    }

    private void Close()
    {
        try { _writer?.Flush(); } catch (Exception ex) { ReportInternalFailure(ex); }
        try { _writer?.Dispose(); } catch (Exception ex) { ReportInternalFailure(ex); }
        try { _stream?.Dispose(); } catch (Exception ex) { ReportInternalFailure(ex); }

        _writer = null;
        _stream = null;
        Volatile.Write(ref _unflushed, 0);
    }

    /// <summary>Deletes the oldest files until at most <paramref name="keep"/> remain.</summary>
    private void Prune(int keep)
    {
        try
        {
            var files = Directory.GetFiles(_directory, "mosaic-*.log")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep < 0 ? 0 : keep);

            foreach (var file in files)
            {
                try { file.Delete(); }
                catch (Exception ex) { ReportInternalFailure(ex); }
            }
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
    }

    /// <summary>
    /// The logger's own failure path. It must never call <see cref="Log"/>'s write path: a disk
    /// failure would then log an error, which would fail, which would log an error.
    /// <see cref="Log.NoteInternalFailure"/> only stores a counter and a string.
    /// </summary>
    private static void ReportInternalFailure(Exception ex) => Log.NoteInternalFailure("sink", ex);

    /// <inheritdoc cref="ReportInternalFailure(Exception)"/>
    private static void ReportInternalFailure(string detail) => Log.NoteInternalFailure("sink", detail);
}
