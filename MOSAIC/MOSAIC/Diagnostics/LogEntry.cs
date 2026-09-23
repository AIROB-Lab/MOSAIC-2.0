using System;
using System.Globalization;

namespace MOSAIC.Diagnostics;

/// <summary>
/// One log record, immutable from the moment the call site builds it.
/// </summary>
/// <remarks>
/// A <see langword="readonly record struct"/> rather than a class: an entry travels from a producer
/// thread through a bounded channel to the writer thread and then into the panel's ring, and a
/// struct keeps that journey free of per-entry heap objects beyond the message string itself.
/// </remarks>
/// <param name="Sequence">Monotonic id handed out by <see cref="Log"/>. The panel's read cursor.</param>
/// <param name="TimestampLocal">Local wall-clock time, because the reader is a student at the rig.</param>
/// <param name="Level">Severity. Never <see cref="LogLevel.None"/>.</param>
/// <param name="Category">Component name, e.g. <c>Delsys</c> or <c>Delsys:Scan</c>.</param>
/// <param name="Instance">Block instance name when there can be more than one, otherwise null.</param>
/// <param name="Message">The already-formatted message body.</param>
public readonly record struct LogEntry(
    long Sequence,
    DateTime TimestampLocal,
    LogLevel Level,
    string Category,
    string? Instance,
    string Message)
{
    /// <summary>
    /// The bracketed component tag, e.g. <c>[Delsys:Scan]</c> or <c>[SupervisedUMAP 'umap1']</c>.
    /// </summary>
    /// <remarks>
    /// Built here so that a migrated call site can drop the prefix it used to hand-write into its
    /// <c>Console.WriteLine</c> string and still produce byte-identical output.
    /// </remarks>
    public string Prefix { get; } = string.IsNullOrEmpty(Instance)
        ? "[" + Category + "]"
        : "[" + Category + " '" + Instance + "']";

    /// <summary>Time column for the panel, <c>HH:mm:ss.fff</c>.</summary>
    /// <remarks>
    /// Computed on read rather than stored: the panel shows at most a screenful, while every entry
    /// would otherwise pay for a string it will probably never display.
    /// </remarks>
    public string TimeText => TimestampLocal.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>
    /// The exact line the file sink appends, without a trailing newline.
    /// </summary>
    /// <remarks>
    /// Embedded newlines (a stack trace) are re-indented rather than left flush left, so a
    /// line-oriented reader such as <c>Select-String</c> still sees one record per timestamp while
    /// the continuation stays visually attached to it.
    /// </remarks>
    public string ToLogLine()
    {
        var message = Message;
        if (message.IndexOf('\n') >= 0)
        {
            message = message.Replace("\r\n", "\n").Replace("\n", "\n    ");
        }

        return string.Concat(
            TimestampLocal.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            " ", LevelText(Level), " ", Prefix, " ", message);
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "     "
    };
}
