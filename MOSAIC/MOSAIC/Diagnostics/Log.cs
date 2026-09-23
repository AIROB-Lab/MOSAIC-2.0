using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MOSAIC.Diagnostics;

/// <summary>
/// The application-wide logger, reached as a static facade from anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a static facade and not an injected <c>ILogger</c>.</b> Almost every diagnostic in this
/// app lives in a plain model class built by <c>BlockFactory</c>, not resolved from the DI
/// container. Threading a logger through those constructors would be a far larger change than the
/// logging itself, so the access route is a static and the cost of that decision is paid here: the
/// facade must be safe to touch from any thread, at any time, including before Avalonia exists.
/// </para>
/// <para>
/// <b>What a call site costs.</b> A disabled level is one <c>Volatile.Read</c> and a branch - the
/// interpolated-string handlers make the compiler skip the interpolation entirely (see
/// <see cref="DebugLogHandler"/>). An enabled level formats one string, takes one
/// <c>Interlocked.Increment</c> for the sequence number and does one non-blocking channel write.
/// No lock, no disk, no UI marshalling: all of that belongs to the sink's writer thread and to the
/// panel's own 30 fps pull.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> A logger that faults while reporting a fault is worse than no logger,
/// so every public entry point swallows, and the internal failure path only touches a counter and a
/// string (<see cref="InternalFailureCount"/>, <see cref="LastInternalError"/>) rather than logging -
/// otherwise a disk failure would recurse forever. Silence is not an option though: those two are
/// what lets the panel say that the logger itself has stopped working.
/// </para>
/// </remarks>
public static class Log
{
    private const string EnvironmentLevelVariable = "MOSAIC_LOG_LEVEL";

    /// <summary>Entries kept aside while there is no file yet. Written to the file once one opens.</summary>
    private const int PreInitCapacity = 256;

    private static readonly object InitGate = new();
    private static readonly LogRing SharedRing = new();
    private static readonly LogEntry[] PreInitBuffer = new LogEntry[PreInitCapacity];
    private static readonly int[] PreInitFilled = new int[PreInitCapacity];

    private static int _minimumLevel = (int)DefaultMinimumLevel;
    private static long _sequence;
    private static long _internalFailures;
    private static string? _lastInternalError;
    private static int _preInitCount;
    private static int _preInitFlushed;
    private static volatile bool _preInitOpen = true;
    private static LogFileSink? _sink;
    private static bool _initialized;
    private static int _handlersInstalled;

    private static LogLevel DefaultMinimumLevel =>
#if DEBUG
        LogLevel.Debug;
#else
        LogLevel.Info;
#endif

    /// <summary>
    /// The capture level: entries below it are never formatted, never queued and never seen.
    /// </summary>
    /// <remarks>
    /// Startup state - the build default plus the <c>MOSAIC_LOG_LEVEL</c> environment variable, both
    /// applied by <see cref="Initialize()"/>. The panel's dropdown does not write it: that dropdown
    /// filters the view over <see cref="Ring"/>, which retains its last <see cref="LogRing.Capacity"/>
    /// entries, so widening the filter brings back entries that were captured all along. Narrowing it
    /// therefore never costs the file the context a bug report needs.
    /// </remarks>
    public static LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref _minimumLevel);
        set
        {
            var clamped = value < LogLevel.Debug ? LogLevel.Debug : value > LogLevel.None ? LogLevel.None : value;
            Volatile.Write(ref _minimumLevel, (int)clamped);
        }
    }

    /// <summary>The whole level gate: one volatile read and a comparison, inlined into the caller.</summary>
    /// <param name="level">Level a call site is about to log at.</param>
    /// <returns>True when the entry would be kept.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(LogLevel level) => (int)level >= Volatile.Read(ref _minimumLevel);

    /// <summary>Folder the log files live in, or null when it could not be created.</summary>
    public static string? LogDirectory { get; private set; }

    /// <summary>Full path of the file currently being written, or null when there is no file.</summary>
    public static string? LogFilePath => _sink?.CurrentPath;

    /// <summary>
    /// The bounded tail the in-app panel reads. Never null and usable before
    /// <see cref="Initialize()"/>, so an early entry is still visible once the panel opens.
    /// </summary>
    public static LogRing Ring => SharedRing;

    /// <summary>Entries discarded because the queue was full. Surfaced in the panel's toolbar.</summary>
    public static long DroppedCount => _sink?.Dropped ?? 0;

    /// <summary>
    /// How many failures the logger has hit inside itself - a disk that filled up, a folder that was
    /// taken away, a writer that faulted.
    /// </summary>
    /// <remarks>
    /// Counted in every configuration, not just Debug. The whole point of this subsystem is that a
    /// failure is visible to the student at the rig, and a logger that dies quietly is the exact bug
    /// it exists to prevent, so this is a supported UI signal: monotonic, allocation-free and safe to
    /// read at frame rate from any thread.
    /// </remarks>
    public static long InternalFailureCount => Interlocked.Read(ref _internalFailures);

    /// <summary>
    /// Description of the most recent internal failure, or null while there has been none.
    /// </summary>
    public static string? LastInternalError => Volatile.Read(ref _lastInternalError);

    /// <summary>
    /// True once the file writer has given up for good: the queue is no longer drained, so nothing
    /// further reaches the FILE until the next <see cref="Initialize()"/>. The ring and the panel
    /// keep working - <see cref="Write"/> treats a stopped writer as a refused entry and retains it
    /// in <see cref="Ring"/> instead.
    /// </summary>
    /// <remarks>
    /// Exposed here so the panel can tell "the file is dead" apart from "nothing has been logged
    /// lately" without knowing the sink type. One volatile read, no allocation: safe to poll from the
    /// UI thread at frame rate. False while there is no file at all - the ring is then the whole
    /// logger and it is still working.
    /// </remarks>
    public static bool WriterStopped => _sink?.WriterStopped ?? false;

    /// <summary>
    /// Opens the log file under <c>%AppData%/MOSAIC/logs</c> and installs the crash hooks.
    /// Idempotent, never throws, and safe to call from any thread before Avalonia exists.
    /// </summary>
    public static void Initialize() => Initialize(null);

    /// <summary>
    /// As <see cref="Initialize()"/>, but writes into <paramref name="directoryOverride"/>.
    /// </summary>
    /// <param name="directoryOverride">
    /// Folder to log into, or null for the real <c>%AppData%</c> location. A non-null value is a
    /// test seam and deliberately re-opens the sink even if the logger is already running, so a test
    /// never has to reach the developer's real application-data folder.
    /// </param>
    public static void Initialize(string? directoryOverride)
    {
        try
        {
            lock (InitGate)
            {
                if (_initialized && directoryOverride is null) return;
                _initialized = true;

                CloseSink();
                ApplyEnvironmentLevel();

                LogDirectory = ResolveDirectory(directoryOverride);
                if (LogDirectory is not null)
                {
                    try
                    {
                        _sink = new LogFileSink(LogDirectory);
                        FlushPreInit(_sink);
                    }
                    catch (Exception ex)
                    {
                        _sink = null;
                        ReportInternalFailure(ex);
                    }
                }
            }

            InstallGlobalExceptionHandlers();

            Info("Log", $"MOSAIC {AppVersion()} on {RuntimeInformation.OSDescription} - log: {LogFilePath ?? "(memory only)"}");
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
    }

    /// <summary>
    /// Routes process-level faults into the log so a crash in the shipped GUI build leaves a trace.
    /// Idempotent; called by <see cref="Initialize()"/>.
    /// </summary>
    /// <remarks>
    /// The published binary is a Windows GUI subsystem executable with no console attached, so
    /// without this an unhandled exception vanishes without a word.
    /// </remarks>
    public static void InstallGlobalExceptionHandlers()
    {
        if (Interlocked.CompareExchange(ref _handlersInstalled, 1, 0) != 0) return;

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    Error("Crash", ex, "Unhandled exception");
                }
                else
                {
                    Error("Crash", "Unhandled non-exception fault: " + e.ExceptionObject);
                }

                // The process is about to die, so the queue has to be emptied here rather than on
                // the writer task, which may never be scheduled again.
                FlushCritical(TimeSpan.FromSeconds(2));
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                // Deliberately no SetObserved: changing whether the process survives is a behaviour
                // change, and this hook exists only to make the fault visible.
                Error("Crash", e.Exception, "Unobserved task exception");
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
    }

    /// <summary>
    /// Drains the queue and flushes the file on the CALLING thread, so the lines describing a crash
    /// reach disk even when the writer task can no longer run. Gives up after
    /// <paramref name="timeout"/>. Never throws.
    /// </summary>
    /// <param name="timeout">How long to wait for the file to become available.</param>
    public static void FlushCritical(TimeSpan timeout)
    {
        try
        {
            _sink?.FlushCritical(timeout);
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
    }

    /// <summary>Flushes, stops the writer and closes the file. Idempotent, never throws.</summary>
    public static void Shutdown()
    {
        try
        {
            lock (InitGate)
            {
                CloseSink();
                _initialized = false;
            }
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
    }

    /// <summary>Logs a per-packet detail.</summary>
    /// <param name="category">Component name, used as the bracketed prefix.</param>
    /// <param name="message">Already-built message.</param>
    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, null, message);

    /// <summary>Logs a lifecycle event.</summary>
    /// <param name="category">Component name, used as the bracketed prefix.</param>
    /// <param name="message">Already-built message.</param>
    public static void Info(string category, string message) => Write(LogLevel.Info, category, null, message);

    /// <summary>Logs a recoverable problem.</summary>
    /// <param name="category">Component name, used as the bracketed prefix.</param>
    /// <param name="message">Already-built message.</param>
    public static void Warn(string category, string message) => Write(LogLevel.Warn, category, null, message);

    /// <summary>Logs a failed operation.</summary>
    /// <param name="category">Component name, used as the bracketed prefix.</param>
    /// <param name="message">Already-built message.</param>
    public static void Error(string category, string message) => Write(LogLevel.Error, category, null, message);

    /// <summary>Logs a per-packet detail for one named block instance.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="instance">Block instance name, or null.</param>
    /// <param name="message">Already-built message.</param>
    public static void Debug(string category, string? instance, string message) => Write(LogLevel.Debug, category, instance, message);

    /// <summary>Logs a lifecycle event for one named block instance.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="instance">Block instance name, or null.</param>
    /// <param name="message">Already-built message.</param>
    public static void Info(string category, string? instance, string message) => Write(LogLevel.Info, category, instance, message);

    /// <summary>Logs a recoverable problem for one named block instance.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="instance">Block instance name, or null.</param>
    /// <param name="message">Already-built message.</param>
    public static void Warn(string category, string? instance, string message) => Write(LogLevel.Warn, category, instance, message);

    /// <summary>Logs a failed operation for one named block instance.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="instance">Block instance name, or null.</param>
    /// <param name="message">Already-built message.</param>
    public static void Error(string category, string? instance, string message) => Write(LogLevel.Error, category, instance, message);

    /// <summary>Logs an interpolated per-packet detail; the interpolation is skipped when off.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="message">Interpolated message, evaluated only if the level is enabled.</param>
    public static void Debug(string category, ref DebugLogHandler message)
    {
        if (message.Enabled) Write(LogLevel.Debug, category, null, message.ToStringAndClear());
    }

    /// <summary>Logs an interpolated lifecycle event; the interpolation is skipped when off.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="message">Interpolated message, evaluated only if the level is enabled.</param>
    public static void Info(string category, ref InfoLogHandler message)
    {
        if (message.Enabled) Write(LogLevel.Info, category, null, message.ToStringAndClear());
    }

    /// <summary>Logs an interpolated warning; the interpolation is skipped when off.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="message">Interpolated message, evaluated only if the level is enabled.</param>
    public static void Warn(string category, ref WarnLogHandler message)
    {
        if (message.Enabled) Write(LogLevel.Warn, category, null, message.ToStringAndClear());
    }

    /// <summary>Logs an interpolated error; the interpolation is skipped when off.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="message">Interpolated message, evaluated only if the level is enabled.</param>
    public static void Error(string category, ref ErrorLogHandler message)
    {
        if (message.Enabled) Write(LogLevel.Error, category, null, message.ToStringAndClear());
    }

    /// <summary>Logs an interpolated per-packet detail for one named block instance.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="instance">Block instance name, or null.</param>
    /// <param name="message">Interpolated message, evaluated only if the level is enabled.</param>
    public static void Debug(string category, string? instance, ref DebugLogHandler message)
    {
        if (message.Enabled) Write(LogLevel.Debug, category, instance, message.ToStringAndClear());
    }

    /// <summary>Logs an interpolated lifecycle event for one named block instance.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="instance">Block instance name, or null.</param>
    /// <param name="message">Interpolated message, evaluated only if the level is enabled.</param>
    public static void Info(string category, string? instance, ref InfoLogHandler message)
    {
        if (message.Enabled) Write(LogLevel.Info, category, instance, message.ToStringAndClear());
    }

    /// <summary>Logs an interpolated warning for one named block instance.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="instance">Block instance name, or null.</param>
    /// <param name="message">Interpolated message, evaluated only if the level is enabled.</param>
    public static void Warn(string category, string? instance, ref WarnLogHandler message)
    {
        if (message.Enabled) Write(LogLevel.Warn, category, instance, message.ToStringAndClear());
    }

    /// <summary>Logs an interpolated error for one named block instance.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="instance">Block instance name, or null.</param>
    /// <param name="message">Interpolated message, evaluated only if the level is enabled.</param>
    public static void Error(string category, string? instance, ref ErrorLogHandler message)
    {
        if (message.Enabled) Write(LogLevel.Error, category, instance, message.ToStringAndClear());
    }

    /// <summary>Logs a failure together with the exception's type, message and stack.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="ex">Exception to append.</param>
    /// <param name="message">What was being attempted.</param>
    public static void Error(string category, Exception ex, string message)
    {
        if (!IsEnabled(LogLevel.Error)) return;
        Write(LogLevel.Error, category, null, Describe(message, ex));
    }

    /// <summary>Logs a failure for one named block instance, with the exception appended.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="instance">Block instance name, or null.</param>
    /// <param name="ex">Exception to append.</param>
    /// <param name="message">What was being attempted.</param>
    public static void Error(string category, string? instance, Exception ex, string message)
    {
        if (!IsEnabled(LogLevel.Error)) return;
        Write(LogLevel.Error, category, instance, Describe(message, ex));
    }

    /// <summary>Logs an interpolated failure with the exception appended.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="ex">Exception to append.</param>
    /// <param name="message">Interpolated message, evaluated only if the level is enabled.</param>
    public static void Error(string category, Exception ex, ref ErrorLogHandler message)
    {
        if (message.Enabled) Write(LogLevel.Error, category, null, Describe(message.ToStringAndClear(), ex));
    }

    /// <summary>Logs an interpolated failure for one named block instance, with the exception appended.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="instance">Block instance name, or null.</param>
    /// <param name="ex">Exception to append.</param>
    /// <param name="message">Interpolated message, evaluated only if the level is enabled.</param>
    public static void Error(string category, string? instance, Exception ex, ref ErrorLogHandler message)
    {
        if (message.Enabled) Write(LogLevel.Error, category, instance, Describe(message.ToStringAndClear(), ex));
    }

    /// <summary>Logs a recoverable problem together with the exception that caused it.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="ex">Exception to append.</param>
    /// <param name="message">What was being attempted.</param>
    public static void Warn(string category, Exception ex, string message)
    {
        if (!IsEnabled(LogLevel.Warn)) return;
        Write(LogLevel.Warn, category, null, Describe(message, ex));
    }

    /// <summary>Logs a recoverable problem for one named block instance, with the exception appended.</summary>
    /// <param name="category">Component name.</param>
    /// <param name="instance">Block instance name, or null.</param>
    /// <param name="ex">Exception to append.</param>
    /// <param name="message">What was being attempted.</param>
    public static void Warn(string category, string? instance, Exception ex, string message)
    {
        if (!IsEnabled(LogLevel.Warn)) return;
        Write(LogLevel.Warn, category, instance, Describe(message, ex));
    }

    /// <summary>Hands out the next entry id. Used by the sink for the entries it synthesises.</summary>
    internal static long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>
    /// Records a failure inside the logger itself, which is never reported through the logger.
    /// </summary>
    /// <param name="source">Which half failed, <c>Log</c> or <c>sink</c>.</param>
    /// <param name="ex">The failure.</param>
    /// <remarks>
    /// A counter and a string, no queue and no file: this is the one path that must still work when
    /// the rest of the logger does not, and anything richer would risk re-entering it.
    /// </remarks>
    internal static void NoteInternalFailure(string source, Exception ex)
    {
        string detail;
        try
        {
            detail = ex.GetType().Name + ": " + ex.Message;
        }
        catch
        {
            // A failure while describing a failure: keep the count honest and say nothing more.
            detail = "unprintable exception";
        }

        NoteInternalFailure(source, detail);
    }

    /// <summary>Records a failure inside the logger using an already formatted description.</summary>
    /// <param name="source">Which half failed, <c>Log</c> or <c>sink</c>.</param>
    /// <param name="detail">What went wrong.</param>
    internal static void NoteInternalFailure(string source, string detail)
    {
        Interlocked.Increment(ref _internalFailures);

        try
        {
            var text = DateTime.Now.ToString("HH:mm:ss") + " " + source + ": " + detail;
            Volatile.Write(ref _lastInternalError, text);
            System.Diagnostics.Debug.WriteLine("[Log] internal failure: " + text);
        }
        catch
        {
            // Nothing left to report to; the counter above is what the panel needs.
        }
    }

    private static void Write(LogLevel level, string category, string? instance, string message)
    {
        if (!IsEnabled(level)) return;

        try
        {
            var entry = new LogEntry(
                Interlocked.Increment(ref _sequence),
                DateTime.Now,
                level,
                category ?? string.Empty,
                string.IsNullOrEmpty(instance) ? null : instance,
                message ?? string.Empty);

            // With no usable file the ring is the whole logger, so the panel still works and the
            // producer still never blocks. Three ways to get there: no sink yet (before Initialize,
            // or because the folder could not be created), a sink whose writer has given up for good
            // - its channel would still accept the entry, and nobody would ever drain it - or a full
            // queue. The WriterStopped check is one volatile int read on a local that is already in
            // hand, so the healthy path pays a read and a predictable branch, and no allocation.
            var sink = _sink;
            if (sink is null || sink.WriterStopped || !sink.TryEnqueue(entry))
            {
                SharedRing.Add(entry);
                if (sink is null && _preInitOpen) BufferPreInit(entry);
            }
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
    }

    /// <summary>
    /// Keeps an entry logged before the file existed, so it is not missing from the file the student
    /// eventually sends. Lock-free by design: a producer must not start blocking just because the
    /// logger has not been initialised yet.
    /// </summary>
    private static void BufferPreInit(in LogEntry entry)
    {
        var slot = Interlocked.Increment(ref _preInitCount) - 1;
        if (slot < 0 || slot >= PreInitCapacity) return;

        PreInitBuffer[slot] = entry;

        // Publishes the struct's fields to whichever thread flushes: it reads only marked slots, so
        // it can never see a half-written entry.
        Volatile.Write(ref PreInitFilled[slot], 1);
    }

    /// <summary>
    /// Writes what was logged before the sink opened into the new file, oldest first, preceded by a
    /// marker counting the entries this file will not contain. Called under <see cref="InitGate"/>;
    /// runs once.
    /// </summary>
    private static void FlushPreInit(LogFileSink sink)
    {
        if (Interlocked.Exchange(ref _preInitFlushed, 1) != 0) return;
        _preInitOpen = false;

        var total = Volatile.Read(ref _preInitCount);
        if (total <= 0) return;

        var kept = total < PreInitCapacity ? total : PreInitCapacity;

        // Snapshot the publication markers before copying anything: a slot whose producer publishes
        // it between the two passes must not be both counted as lost and written to the file.
        Span<bool> published = stackalloc bool[kept];
        var unpublished = 0;
        for (var i = 0; i < kept; i++)
        {
            published[i] = Volatile.Read(ref PreInitFilled[i]) != 0;
            if (!published[i]) unpublished++;
        }

        // Two ways an entry misses this file: the buffer overflowed past it, or its producer had not
        // finished publishing it when the sink opened. Both are counted, because an entry that
        // reaches neither the file nor this marker disappears with nothing to show it ever existed.
        var missing = total - kept + unpublished;

        var backlog = new LogEntry[kept + 1];
        var count = 0;

        if (missing > 0)
        {
            backlog[count++] = new LogEntry(
                NextSequence(),
                DateTime.Now,
                LogLevel.Warn,
                "Log",
                null,
                $"{missing} entries logged before initialisation were not kept for this file");
        }

        for (var i = 0; i < kept; i++)
        {
            if (!published[i]) continue;

            backlog[count++] = PreInitBuffer[i];
            PreInitBuffer[i] = default;
        }

        sink.WriteBacklog(backlog, count);
    }

    private static string Describe(string message, Exception ex)
    {
        try
        {
            return message + "\n" + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace;
        }
        catch (Exception inner)
        {
            ReportInternalFailure(inner);
            return message;
        }
    }

    private static string? ResolveDirectory(string? directoryOverride)
    {
        try
        {
            var directory = directoryOverride;
            if (string.IsNullOrWhiteSpace(directory))
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                directory = Path.Combine(appData, "MOSAIC", "logs");
            }

            // Unlike the other %AppData% users in Services/, this creation is guarded: it runs as
            // the first statement of App.Initialize, where a throw would take the app down before
            // it drew a pixel. Without a folder the logger degrades to the in-memory ring.
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
            return null;
        }
    }

    private static void ApplyEnvironmentLevel()
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable(EnvironmentLevelVariable);
            if (string.IsNullOrWhiteSpace(configured)) return;
            if (Enum.TryParse<LogLevel>(configured.Trim(), ignoreCase: true, out var level))
            {
                MinimumLevel = level;
            }
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
    }

    private static void CloseSink()
    {
        var sink = _sink;
        if (sink is null) return;

        _sink = null;
        try
        {
            sink.FlushCritical(TimeSpan.FromSeconds(1));
            sink.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
        }
    }

    private static string AppVersion()
    {
        try
        {
            return typeof(Log).Assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch (Exception ex)
        {
            ReportInternalFailure(ex);
            return "unknown";
        }
    }

    private static void ReportInternalFailure(Exception ex) => NoteInternalFailure("Log", ex);
}
