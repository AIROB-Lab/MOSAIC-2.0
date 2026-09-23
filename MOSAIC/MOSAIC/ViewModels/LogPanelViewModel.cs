using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Diagnostics;
using MOSAIC.Services;

namespace MOSAIC.ViewModels;

/// <summary>
/// Feeds the in-app log panel from <see cref="Log.Ring"/>.
/// </summary>
/// <remarks>
/// <para>
/// The panel pulls; the logger never pushes. Producers only ever write to the sink's channel, the
/// sink's single writer thread fills the ring, and this view model copies whatever is new on the
/// existing <see cref="VisualizationTimer"/> tick — so no data thread ever touches the bound
/// collection, and no second timer is created for the panel.
/// </para>
/// <para>
/// The result is a tail view rather than a transcript: while the panel is closed, or while ticks are
/// suspended for a resize drag, the ring simply overwrites and the panel resumes at the newest
/// entries. The log file is the complete record.
/// </para>
/// </remarks>
public sealed partial class LogPanelViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Hard cap on the bound collection, so a long session cannot grow the UI without limit.
    /// </summary>
    /// <remarks>
    /// Half the ring, because the panel is the short tail and not the archive: the ring still holds
    /// <see cref="LogRing.DefaultCapacity"/> entries and the file holds every one of them, while this
    /// cap is what bounds the rows the list keeps alive and the text the Copy button builds.
    /// </remarks>
    public const int MaxVisibleEntries = 1000;

    /// <summary>
    /// Upper bound on entries appended per tick, so one burst costs a bounded amount of UI work
    /// instead of a visible stall.
    /// </summary>
    private const int MaxEntriesPerTick = 256;

    /// <summary>
    /// How long the note about a recovered failure stays up, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Long enough for a student who was not watching the panel to notice it, short enough that a
    /// logger which has been working ever since stops saying otherwise. It runs from the tick the
    /// failure was seen on rather than the one it happened on, so a panel opened afterwards still
    /// gets the full window.
    /// </remarks>
    private const long TransientNoticeMs = 30_000;

    private readonly List<LogEntry> _scratch = new(MaxEntriesPerTick);
    private readonly List<LogEntry> _matching = new(MaxEntriesPerTick);
    private long _cursor;
    private long _reportedFailures;

    /// <summary><see cref="Environment.TickCount64"/> at which the transient note clears, or 0 when
    /// there is no note to clear — a stopped writer's banner never gets a deadline.</summary>
    private long _noticeExpiryTicks;

    private bool _running;

    /// <summary>The rows the panel shows, newest last. Only ever touched on the UI thread.</summary>
    /// <remarks>
    /// An <see cref="AvaloniaList{T}"/> rather than an <c>ObservableCollection</c> for its range
    /// operations: a tick appends its whole batch and trims the overflow in one notification each,
    /// instead of one per row that the list would have to re-lay-out.
    /// </remarks>
    public AvaloniaList<LogEntry> Entries { get; } = new();

    /// <summary>The levels offered by the toolbar dropdown. <see cref="LogLevel.None"/> is not one of
    /// them — a panel that shows nothing has no reason to be open.</summary>
    public IReadOnlyList<LogLevel> Levels { get; } = new[]
    {
        LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error
    };

    /// <summary>
    /// Which of the captured entries the panel displays. A view filter, never the capture level.
    /// </summary>
    /// <remarks>
    /// The two are deliberately separate. Narrowing this to <see cref="LogLevel.Error"/> to quieten
    /// the panel must not stop Info and Warn reaching the file, because that is exactly the context a
    /// bug report needs; widening it again reveals entries that were captured all along, since the
    /// filter runs over <see cref="Log.Ring"/> rather than over the gate. What gets captured is
    /// <see cref="Log.MinimumLevel"/>, fixed at startup, and nothing in this panel writes it.
    /// </remarks>
    [ObservableProperty]
    private LogLevel _viewLevel = LogLevel.Debug;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDropped))]
    private long _droppedCount;

    /// <summary>Whether the queue has ever overflowed, which is what the toolbar counter announces.</summary>
    public bool HasDropped => DroppedCount > 0;

    /// <summary>
    /// What went wrong inside the logger itself, or null while it is healthy.
    /// </summary>
    /// <remarks>
    /// The one failure the log file cannot report, because the file is what failed. Two very
    /// different things route through the same counter, which is what <see cref="IsWriterStopped"/>
    /// separates: a writer that has given up, after which the panel really is the only record left,
    /// and a single stumble the logger carried on from — rotation failing to delete an old file
    /// because the student has it open in an editor is the ordinary one, since the sink shares the
    /// file for reading — after which the file is still being written.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogFailure))]
    private string? _logFailureDetail;

    /// <summary>Whether the failure banner shows. False for the whole of a healthy session.</summary>
    public bool HasLogFailure => LogFailureDetail is not null;

    /// <summary>
    /// Whether the file writer has stopped for good, which decides both what the banner claims and
    /// whether it is allowed to clear itself.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogFailureHeadline))]
    private bool _isWriterStopped;

    /// <summary>The banner's headline, in the student's terms: what happened and what to do.</summary>
    /// <remarks>
    /// The strong claim is spent only on a stopped writer. A banner that announced the end of the log
    /// file on every internal failure would spend most sessions doing it over a logger that was
    /// working perfectly well, and a student who has learned to ignore that one ignores the real one
    /// with it.
    /// </remarks>
    public string LogFailureHeadline => IsWriterStopped ? WriterStoppedHeadline : TransientFailureHeadline;

    private const string WriterStoppedHeadline =
        "Log file stopped. The entries below are the only record left, so use Copy to keep them.";

    private const string TransientFailureHeadline =
        "Logging hit a problem and carried on. The log file is still being written. " +
        "If it keeps happening, use Copy and include the note below in your report.";

    /// <summary>Full path of the current log file, for the toolbar tooltip.</summary>
    public string LogFilePathText => Log.LogFilePath ?? "(file unavailable)";

    /// <summary>
    /// What the sink is recording, so the dropdown's tooltip can say plainly which of the two levels
    /// the dropdown is not touching.
    /// </summary>
    /// <remarks>
    /// Read on demand rather than observed: the capture level is startup state — the build default
    /// plus the <c>MOSAIC_LOG_LEVEL</c> override — and no UI writes it.
    /// </remarks>
    public string CaptureLevelText =>
        $"Shows captured entries at this level and above. Capture level is {Log.MinimumLevel} (set at startup).";

    partial void OnViewLevelChanged(LogLevel value) => RebuildFromRing();

    /// <summary>Subscribes to the shared 30 fps tick. Idempotent.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;

        // Show what the ring already holds, so opening the panel after something went wrong still
        // explains it instead of starting blank.
        RebuildFromRing();
        RefreshHealth();
        VisualizationTimer.Instance.Subscribe(OnTick, VisualizationTimer.TickRate.Fps30);
    }

    /// <summary>Unsubscribes. Idempotent. The timer stops itself once nobody is left.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        VisualizationTimer.Instance.Unsubscribe(OnTick);
    }

    /// <summary>Every visible row as text, for the Copy button.</summary>
    public string BuildClipboardText()
    {
        var builder = new StringBuilder(Entries.Count * 96);
        foreach (var entry in Entries)
            builder.AppendLine(entry.ToLogLine());
        return builder.ToString();
    }

    public void Dispose() => Stop();

    /// <remarks>
    /// Must never log from here: <see cref="VisualizationTimer"/> reports a subscriber's exception, so
    /// a panel that logged from inside its own tick would feed itself. Reading the logger's state, as
    /// <see cref="RefreshHealth"/> does, is fine; writing to it is not.
    /// </remarks>
    private void OnTick()
    {
        _scratch.Clear();
        _cursor = Log.Ring.CopyFrom(_cursor, _scratch, MaxEntriesPerTick);

        // The cursor advances over every captured entry whatever the filter says, so narrowing the
        // view never makes the panel fall behind the ring.
        if (_scratch.Count > 0) Append(SelectMatching(_scratch));

        RefreshHealth();
    }

    /// <summary>
    /// Pulls the logger's own health onto the bound properties, on the tick the panel already has.
    /// </summary>
    /// <remarks>
    /// A healthy tick pays two interlocked reads — the dropped counter the toolbar shows and
    /// <see cref="Log.InternalFailureCount"/> — the equality check the generated
    /// <see cref="DroppedCount"/> setter makes before it raises anything, and one field comparison
    /// for the notice deadline. Everything else, the message and <see cref="Log.WriterStopped"/>
    /// included, is reached only on the tick a new failure lands on, and the counter never goes back
    /// down, so that is once per failure rather than once per tick.
    /// </remarks>
    private void RefreshHealth()
    {
        DroppedCount = Log.DroppedCount;

        var failures = Log.InternalFailureCount;
        if (failures != _reportedFailures)
        {
            _reportedFailures = failures;

            // Reading the flag only here is enough: the sink sets it and then reports a failure of
            // its own, so a writer that gives up always arrives with a count change to be seen on.
            IsWriterStopped = Log.WriterStopped;

            // Shown verbatim, timestamp and all: this line is what a bug report needs, and rewording
            // it here would only lose the detail that identifies the failure.
            LogFailureDetail = Log.LastInternalError ?? "No further detail was recorded.";

            // A stopped writer is permanent, so its banner stays; anything the logger recovered from
            // gets a deadline instead, because a working logger must not go on wearing a warning.
            _noticeExpiryTicks = IsWriterStopped ? 0 : Environment.TickCount64 + TransientNoticeMs;
            return;
        }

        if (_noticeExpiryTicks == 0 || Environment.TickCount64 < _noticeExpiryTicks) return;

        _noticeExpiryTicks = 0;
        LogFailureDetail = null;
    }

    private void RebuildFromRing()
    {
        _scratch.Clear();
        _cursor = Log.Ring.CopyFrom(0, _scratch, Log.Ring.Capacity);

        var matching = SelectMatching(_scratch);
        if (matching.Count > MaxVisibleEntries)
            matching.RemoveRange(0, matching.Count - MaxVisibleEntries);

        Entries.Clear();
        Entries.AddRange(matching);
    }

    /// <summary>Narrows a batch to the rows the current view filter admits, into a reused buffer.</summary>
    private List<LogEntry> SelectMatching(List<LogEntry> batch)
    {
        var level = ViewLevel;
        _matching.Clear();

        foreach (var entry in batch)
        {
            if (entry.Level >= level) _matching.Add(entry);
        }

        return _matching;
    }

    /// <summary>
    /// Appends a batch and drops whatever it pushed past <see cref="MaxVisibleEntries"/>, in one
    /// collection change each rather than one per row.
    /// </summary>
    private void Append(List<LogEntry> rows)
    {
        if (rows.Count == 0) return;

        Entries.AddRange(rows);

        var excess = Entries.Count - MaxVisibleEntries;
        if (excess > 0) Entries.RemoveRange(0, excess);
    }

    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        Log.Ring.Clear();
        _cursor = Log.Ring.NextSequence;
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var directory = Log.LogDirectory;
        if (string.IsNullOrEmpty(directory)) return;

        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("LogPanel", ex, "Could not open the log folder");
        }
    }
}
