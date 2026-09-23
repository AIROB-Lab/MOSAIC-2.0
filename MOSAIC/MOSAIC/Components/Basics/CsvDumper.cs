using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Diagnostics;

namespace MOSAIC.Components.Basics;

/// <summary>
/// Asynchronously appends numeric samples to a CSV file using a bounded, non-blocking queue.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CsvDumper"/> is designed for real-time producers (e.g., sensors, signal-processing blocks) that must
/// not block on disk I/O. Calls to <see cref="Enqueue(double,double)"/> and its overloads attempt to write a
/// stable numeric row snapshot into an internal <see cref="Channel{T}"/>.
/// </para>
/// <para>
/// <b>Automatic logging:</b> When a <see cref="CsvDumper"/> is assigned to <see cref="BaseBlock.Dumper"/>,
/// every successful <see cref="BaseBlock.Publish"/> call automatically logs the published value via
/// <see cref="Enqueue(double, object)"/>. This eliminates per-block boilerplate. Blocks no longer need
/// to manage their own dumper fields, enqueue calls, or disposal.
/// </para>
/// <para>
/// <b>Supported types for <see cref="Enqueue(double, object)"/>:</b>
/// <list type="bullet">
///   <item><description><see cref="Vector{T}"/> of <see cref="double"/> — logged as timestamp, v₀, v₁, …</description></item>
///   <item><description><see cref="double"/>[] — logged as timestamp, v₀, v₁, …</description></item>
///   <item><description><see cref="double"/> — logged as timestamp, value</description></item>
///   <item><description><c>ValueTuple&lt;string, Vector&lt;double&gt;&gt;</c> — logged as timestamp, label, v₀, v₁, …</description></item>
///   <item><description><c>ValueTuple&lt;string, double[]&gt;</c> — logged as timestamp, label, v₀, v₁, …</description></item>
///   <item><description><see cref="Matrix{T}"/> of <see cref="double"/> — logged as one row per matrix row (future-ready)</description></item>
///   <item><description><see cref="IEnumerable{T}"/> of <see cref="double"/> — logged as timestamp, v₀, v₁, …</description></item>
/// </list>
/// Unsupported types are silently skipped.
/// </para>
/// <para>
/// <b>Backpressure behavior:</b> The internal channel is bounded (8 192 slots). When full,
/// new rows are rejected and counted in DroppedRows (<see cref="BoundedChannelFullMode.Wait"/>).
/// </para>
/// <para>
/// <b>Flush behavior:</b> The writer flushes periodically (default: 10 s) and always performs a final
/// flush during disposal.
/// </para>
/// <para>
/// <b>Threading:</b> Multiple producers are supported (<c>SingleWriter = false</c>). A single background
/// consumer task performs the file writes (<c>SingleReader = true</c>).
/// </para>
/// <para>
/// <b>Culture:</b> All numeric values use <see cref="CultureInfo.InvariantCulture"/> to ensure a stable
/// decimal separator (<c>.</c>) regardless of system locale.
/// </para>
/// </remarks>
public class CsvDumper : IAsyncDisposable
{
    /// <summary>
    /// One item on the writer queue: either a numeric row snapshot, or a flush request carrying the
    /// completion source to signal once the bytes are on disk.
    /// </summary>
    /// <remarks>
    /// Routing flushes through the same queue as the data is deliberate. The writer loop parks on
    /// <see cref="ChannelReader{T}.WaitToReadAsync"/>, so a flag set from outside would not wake
    /// it — which is precisely the case a flush exists for, a capture that has just stopped
    /// producing. Queueing the request wakes the loop and, because the channel is ordered, also
    /// guarantees the flush happens after every row enqueued before it.
    /// </remarks>
    private readonly record struct CsvCommand(double Timestamp, double Scalar, double[]? Values,
        string? Label = null, int RowIndex = -1, TaskCompletionSource? Flush = null);

    /// <summary>
    /// Full path (including file name) of the output CSV file.
    /// </summary>
    private readonly string _filePath;

    /// <summary>
    /// Bounded channel used as a non-blocking queue between producer threads and the background writer.
    /// </summary>
    private readonly Channel<CsvCommand> _channel;

    /// <summary>
    /// Number of rejected data rows; flush commands wait for space and are never dropped.
    /// </summary>
    private long _droppedRows;
    private Exception? _lastError;
    public long DroppedRows => Interlocked.Read(ref _droppedRows);
    public int QueuedCommands => _channel.Reader.Count;
    public Exception? LastError => Volatile.Read(ref _lastError);

    private void EnqueueCommand(CsvCommand command)
    {
        if (_channel.Writer.TryWrite(command)) return;
        var dropped = Interlocked.Increment(ref _droppedRows);
        // Log exponentially to make loss visible without flooding the log under sustained overload.
        if ((dropped & (dropped - 1)) == 0)
            Log.Warn("CsvDumper", $"'{_filePath}': {dropped} recording row(s) rejected; queue full or writer closed.");
    }

    /// <summary>
    /// Handle to the background writer task spawned in the constructor.
    /// </summary>
    private readonly Task _writerTask;

    /// <summary>
    /// Interval between periodic flushes of the underlying <see cref="StreamWriter"/>.
    /// </summary>
    private readonly TimeSpan _flushEvery;

    /// <summary>
    /// Initializes a new CSV dumper that appends rows to a file on a background writer task.
    /// </summary>
    /// <param name="filePath">Directory path where the CSV file will be created.</param>
    /// <param name="fileName">
    /// Base file name (without extension). The implementation appends <c>.csv</c>.
    /// </param>
    /// <param name="flushEvery">
    /// Flush interval for the underlying <see cref="StreamWriter"/>. If <see langword="null"/>, defaults to 10 seconds.
    /// </param>
    /// <remarks>
    /// <para>
    /// The file is opened in append mode (<see cref="FileMode.Append"/>). The target directory is created
    /// if it does not already exist.
    /// </para>
    /// <para>
    /// The background writer task is started immediately upon construction. Dispose the instance via
    /// <see cref="DisposeAsync"/> to guarantee all queued rows are flushed.
    /// </para>
    /// </remarks>
    public CsvDumper(
        string filePath,
        string fileName = "out.csv",
        TimeSpan? flushEvery = null,
        int queueCapacity = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        if (flushEvery is { } interval && interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(flushEvery));
        _filePath = Path.Combine(filePath, $"{fileName}.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        _flushEvery = flushEvery ?? TimeSpan.FromMilliseconds(10000);

        _channel = Channel.CreateBounded<CsvCommand>(new BoundedChannelOptions(queueCapacity)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _writerTask = Task.Run(RunWriterAsync);
    }

    /// <summary>
    /// Enqueues a CSV row by inspecting the runtime type of <paramref name="value"/> and
    /// formatting it appropriately.
    /// </summary>
    /// <param name="timestamp">UNIX-like timestamp (seconds) written as the first column.</param>
    /// <param name="value">
    /// The published value. Supported types:
    /// <see cref="Vector{T}"/>, <see cref="double"/>[], <see cref="double"/>,
    /// <c>(string, Vector&lt;double&gt;)</c>, <c>(string, double[])</c>,
    /// <see cref="Matrix{T}"/> (one row per matrix row),
    /// and <see cref="IEnumerable{T}"/> of <see cref="double"/>.
    /// Unsupported types are silently skipped.
    /// </param>
    /// <remarks>
    /// <para>
    /// This is the primary entry point used by <see cref="BaseBlock.Publish"/> for automatic CSV logging.
    /// It dispatches to the appropriate typed <c>Enqueue</c> overload based on runtime type checks,
    /// ordered from most-specific to least-specific to avoid ambiguity.
    /// </para>
    /// <para>
    /// For labelled tuples (e.g., from <see cref="MOSAIC.Models.Analytics.OnlineLDA"/>), the label string
    /// is written as the second column (CSV-escaped), followed by the numeric values.
    /// </para>
    /// <para>
    /// <see cref="Matrix{T}"/> values produce multiple CSV rows (one per matrix row), all sharing the
    /// same timestamp. This is intended for future batch-output blocks.
    /// </para>
    /// </remarks>
    public void Enqueue(double timestamp, object value)
    {
        switch (value)
        {
            // ── Labelled tuples (e.g., OnlineLDA publishes (string, Vector<double>)) ──
            case ValueTuple<string, Vector<double>> tv:
                EnqueueLabelled(timestamp, tv.Item1, tv.Item2);
                return;

            case ValueTuple<string, double[]> ta:
                EnqueueLabelled(timestamp, ta.Item1, ta.Item2);
                return;

            case Vector<double> vec:
                Enqueue(timestamp, vec);
                return;

            case double[] arr:
                Enqueue(timestamp, (ReadOnlySpan<double>)arr);
                return;

            case double d:
                Enqueue(timestamp, d);
                return;

            case Matrix<double> mat:
                EnqueueMatrix(timestamp, mat);
                return;

            case IEnumerable<double> seq:
                Enqueue(timestamp, seq);
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// Enqueues a CSV row consisting of a timestamp and a single value.
    /// </summary>
    /// <param name="timestamp">Timestamp value written as the first column.</param>
    /// <param name="value">Sample value written as the second column.</param>
    public void Enqueue(double timestamp, double value)
    {
        EnqueueCommand(new CsvCommand(timestamp, value, null));
    }

    /// <summary>
    /// Enqueues a CSV row consisting of a timestamp and a set of values.
    /// </summary>
    /// <param name="timestamp">Timestamp value written as the first column.</param>
    /// <param name="values">Values written as subsequent columns.</param>
    public void Enqueue(double timestamp, params double[] values)
        => Enqueue(timestamp, (ReadOnlySpan<double>)values);

    /// <summary>
    /// Enqueues a CSV row consisting of a timestamp and a span of values.
    /// </summary>
    /// <param name="timestamp">Timestamp value written as the first column.</param>
    /// <param name="values">Values written as subsequent columns.</param>
    public void Enqueue(double timestamp, ReadOnlySpan<double> values)
    {
        EnqueueCommand(new CsvCommand(timestamp, 0, values.ToArray()));
    }

    /// <summary>
    /// Enqueues a CSV row consisting of a timestamp and a <see cref="Vector{T}"/>.
    /// </summary>
    /// <param name="timestamp">Timestamp value written as the first column.</param>
    /// <param name="vector">Vector whose elements are written as subsequent columns.</param>
    public void Enqueue(double timestamp, Vector<double> vector)
    {
        if (vector is not null) EnqueueCommand(new CsvCommand(timestamp, 0, vector.ToArray()));
    }

    /// <summary>
    /// Enqueues a CSV row consisting of a timestamp and an enumerable of values.
    /// </summary>
    /// <param name="timestamp">Timestamp value written as the first column.</param>
    /// <param name="values">
    /// Values written as subsequent columns. If <see langword="null"/>, only the timestamp column is written.
    /// </param>
    public void Enqueue(double timestamp, IEnumerable<double>? values)
    {
        EnqueueCommand(new CsvCommand(timestamp, 0, values is null ? Array.Empty<double>() : System.Linq.Enumerable.ToArray(values)));
    }

    /// <summary>
    /// Enqueues a labelled CSV row: timestamp, label, v₀, v₁, …
    /// </summary>
    /// <param name="timestamp">Timestamp value written as the first column.</param>
    /// <param name="label">String label written as the second column (CSV-escaped).</param>
    /// <param name="vector">Vector whose elements are written as subsequent columns.</param>
    public void EnqueueLabelled(double timestamp, string label, Vector<double> vector)
    {
        if (vector is not null) EnqueueCommand(new CsvCommand(timestamp, 0, vector.ToArray(), label ?? string.Empty));
    }

    /// <summary>
    /// Enqueues a labelled CSV row: timestamp, label, v₀, v₁, …
    /// </summary>
    /// <param name="timestamp">Timestamp value written as the first column.</param>
    /// <param name="label">String label written as the second column (CSV-escaped).</param>
    /// <param name="values">Array whose elements are written as subsequent columns.</param>
    public void EnqueueLabelled(double timestamp, string label, double[] values)
    {
        if (values is not null) EnqueueCommand(new CsvCommand(timestamp, 0, (double[])values.Clone(), label ?? string.Empty));
    }

    /// <summary>
    /// Enqueues one CSV row per matrix row, all sharing the same timestamp.
    /// </summary>
    /// <param name="timestamp">Timestamp value written as the first column of each row.</param>
    /// <param name="matrix">
    /// Matrix whose rows are each written as a separate CSV line:
    /// <c>timestamp, row, col₀, col₁, …</c>.
    /// </param>
    /// <remarks>
    /// The second column is the zero-based row index to allow reconstruction of the matrix shape.
    /// </remarks>
    public void EnqueueMatrix(double timestamp, Matrix<double> matrix)
    {
        if (matrix is null) return;
        for (int r = 0; r < matrix.RowCount; r++)
        {
            var values = new double[matrix.ColumnCount];
            for (int c = 0; c < values.Length; c++) values[c] = matrix[r, c];
            EnqueueCommand(new CsvCommand(timestamp, 0, values, RowIndex: r));
        }
    }

    /// <summary>
    /// Appends a culture-invariant, high-precision representation of a <see cref="double"/> to a string builder.
    /// Uses <c>G17</c> for full IEEE 754 round-trip precision.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendDouble(StringBuilder sb, double d)
    {
        Span<char> characters = stackalloc char[32];
        if (!d.TryFormat(characters, out int written, "G17", CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Numeric CSV formatting exceeded its buffer.");
        sb.Append(characters[..written]);
    }

    private static void AppendRow(StringBuilder builder, CsvCommand row)
    {
        AppendDouble(builder, row.Timestamp);
        if (row.Label is not null) builder.Append(',').Append(EscapeCsv(row.Label));
        if (row.RowIndex >= 0)
            builder.Append(',').Append(row.RowIndex.ToString(CultureInfo.InvariantCulture));
        if (row.Values is { } values)
        {
            foreach (double value in values) { builder.Append(','); AppendDouble(builder, value); }
        }
        else { builder.Append(','); AppendDouble(builder, row.Scalar); }
        builder.AppendLine();
    }

    private static async Task WritePendingAsync(StreamWriter writer, StringBuilder batch)
    {
        foreach (var chunk in batch.GetChunks()) await writer.WriteAsync(chunk).ConfigureAwait(false);
        batch.Clear();
        // Do not retain a one-off unusually large packet's formatting buffer forever.
        if (batch.Capacity > 1 << 20) batch.Capacity = 1 << 16;
    }

    /// <summary>
    /// Escapes a string for inclusion in a CSV cell.
    /// </summary>
    /// <param name="s">Input string.</param>
    /// <returns>The CSV-escaped string, quoted if needed and with embedded quotes doubled.</returns>
    private static string EscapeCsv(string s)
    {
        bool needsQuotes = s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r');
        return needsQuotes ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>
    /// Background writer loop that drains the internal queue and appends lines to the file.
    /// </summary>
    private async Task RunWriterAsync()
    {
        try { await WriterLoop().ConfigureAwait(false); }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastError, ex);
            _channel.Writer.TryComplete(ex);
            throw;
        }
        finally { _channel.Writer.TryComplete(); }
    }

    private async Task WriterLoop()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                1 << 16,
                useAsync: true);
        }
        catch (Exception ex)
        {
            // A read-only folder or an unreachable path used to leave a zero-byte file and no
            // message at all, on a background thread where nothing could report it. The rethrow
            // keeps the failure signal itself intact: a faulted writer task is what DisposeAsync
            // observes, and returning normally here would make a dumper that never opened a file
            // indistinguishable from one that recorded everything asked of it.
            Log.Error("CsvDumper", ex, $"Could not open '{_filePath}'; nothing will be recorded.");
            throw;
        }

        // Catching the open failure forces the stream to be declared outside the try, so this is
        // what closes the handle if the StreamWriter constructor is the thing that throws.
        using var openStream = stream;
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        var lastFlush = DateTime.UtcNow;
        int linesSinceFlush = 0;
        var batch = new StringBuilder(1 << 16);

        try
        {
            while (true)
            {
                var available = _channel.Reader.WaitToReadAsync().AsTask();
                if (linesSinceFlush > 0)
                {
                    // Keep one pending read while idle. A quiet source must not postpone
                    // flushing indefinitely just because no further sample wakes the writer.
                    var remaining = _flushEvery - (DateTime.UtcNow - lastFlush);
                    try { await available.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        await writer.FlushAsync().ConfigureAwait(false);
                        lastFlush = DateTime.UtcNow;
                        linesSinceFlush = 0;
                    }
                }
                if (!await available.ConfigureAwait(false)) break;

                // Bound a batch so continuous producers cannot starve the periodic flush.
                int batchSize = 0;
                while (batchSize++ < 1024 && _channel.Reader.TryRead(out var cmd))
                {
                    if (cmd.Flush is null)
                    {
                        AppendRow(batch, cmd);
                        linesSinceFlush++;
                        if (batch.Length >= 1 << 16) await WritePendingAsync(writer, batch).ConfigureAwait(false);
                        continue;
                    }

                    // A barrier flush includes the formatted batch preceding it, never later rows.
                    await WritePendingAsync(writer, batch).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                    lastFlush = DateTime.UtcNow;
                    linesSinceFlush = 0;
                    cmd.Flush?.TrySetResult();
                }

                await WritePendingAsync(writer, batch).ConfigureAwait(false);

                // Flush sustained traffic between batches as well as idle traffic above.
                if (linesSinceFlush > 0 && (DateTime.UtcNow - lastFlush) >= _flushEvery)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                    lastFlush = DateTime.UtcNow;
                    linesSinceFlush = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("CsvDumper", ex, $"Recording write failed for '{_filePath}'; the file is incomplete.");
            throw;
        }

        await writer.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a non-droppable barrier after the rows accepted so far and awaits their flush.
    /// Cancellation abandons the wait, not rows already accepted. Writer failures propagate.
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _channel.Writer.WriteAsync(new CsvCommand(0, 0, null, Flush: completion), cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        // A disk failure must complete this wait even if the writer never reaches the barrier.
        var finished = await Task.WhenAny(completion.Task, _writerTask).WaitAsync(cancellationToken).ConfigureAwait(false);
        await finished.ConfigureAwait(false);
    }

    /// <summary>Completes the queue and waits for accepted rows and barriers to finish. Repeatable.</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _writerTask.ConfigureAwait(false); }
        catch (Exception ex)
        {
            Log.Error("CsvDumper", ex, $"Recording writer for '{_filePath}' faulted.");
        }
    }

    /// <summary>
    /// Factory helper for dependency-injection scenarios.
    /// </summary>
    /// <param name="sp">Service provider used to resolve dependencies.</param>
    /// <param name="filePath">Directory path where the CSV file will be created.</param>
    /// <param name="fileName">Base file name (without extension).</param>
    /// <returns>A new <see cref="CsvDumper"/> instance.</returns>
    public static CsvDumper ConfigureInput(IServiceProvider sp, string filePath, string fileName)
    {
        return ActivatorUtilities.CreateInstance<CsvDumper>(sp, filePath, fileName);
    }
}
