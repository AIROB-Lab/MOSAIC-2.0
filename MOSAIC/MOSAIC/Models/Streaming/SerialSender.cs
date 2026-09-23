using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Manager.Serial;
using MOSAIC.Visualization;

namespace MOSAIC.Models.Streaming;

/// <summary>
/// Streams pipeline samples to a serial device as delimited ASCII lines, off the pipeline thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> each incoming sample is formatted into one line — fields joined by
/// <c>separator</c>, closed by <c>terminator</c> — and handed to a bounded queue that a dedicated
/// writer thread drains. Firmware that reads <c>Serial.readStringUntil('\n')</c> and splits on a
/// comma needs no adapter.
/// </para>
/// <para>
/// <b>Why a queue and a thread:</b> serial writes block. A USB-CDC peer that stops draining its
/// buffer stalls the writer for the full <c>writeTimeoutMs</c>, and doing that inline would hold up
/// <see cref="BaseBlock.Publish"/> for every subscriber of the upstream block — see the back-pressure
/// contract on <see cref="BaseBlock"/>, where a slow <c>OnReceive</c> costs samples graph-wide. Here
/// the cost of a stalled port is bounded: it is paid by this block's queue and nothing else.
/// </para>
/// <para>
/// <b>Back-pressure:</b> the queue holds <c>queueCapacity</c> lines. Once full, <c>dropPolicy</c>
/// decides who loses — <see cref="DropPolicy.DropNewest"/> protects ordering and the oldest backlog,
/// <see cref="DropPolicy.DropOldest"/> protects recency. Either way <see cref="Send"/> returns
/// promptly and never blocks the caller, and every discarded sample is counted.
/// </para>
/// <para>
/// <b>Formatting:</b> values are written with <c>ToString("F{decimals}", InvariantCulture)</c>.
/// The invariant culture is not decoration — under a comma-decimal locale such as de-DE the default
/// formatter would emit <c>84,37</c>, which a comma-separated line turns into two fields and a
/// desynchronised parser. Non-finite values are replaced by <c>nonFiniteValue</c> before formatting
/// for the same reason: <c>"NaN"</c> is not a number to most firmware.
/// </para>
/// <para>
/// <b>Reconnection:</b> construction never fails because of hardware. If the port is absent or busy,
/// the block starts disconnected and — when <c>autoReconnect</c> is set — keeps retrying while
/// samples flow through the queue's drop policy. A broken link (any non-timeout failure) closes the
/// port and re-enters that same retry path. Write timeouts deliberately do <i>not</i> close the port:
/// a receiver that is merely slow would otherwise be met with a reconnect storm.
/// </para>
/// <para>
/// <b>Changing port at runtime:</b> <see cref="TryChangePort"/> retargets a running block — the card
/// exposes it as a port picker, so a wrong <c>COM</c> number in the JSON is a click to fix rather
/// than an edit and a reload. The request is validated on the caller's thread and applied on the
/// writer's, which closes the old port and reconnects through the path above. The pipeline is never
/// interrupted: samples keep arriving and keep being published downstream, and the ones that cannot
/// be sent during the changeover are counted as dropped like any others.
/// </para>
/// <para>
/// <b>Reset delay:</b> opening a USB-CDC port asserts DTR, which resets boards like the Arduino;
/// <c>resetDelayMs</c> waits out the bootloader before the first write. Note the interaction with
/// the queue: while the writer waits, samples keep arriving, so at <i>R</i> Hz a settle window
/// discards roughly <c>R × resetDelayMs / 1000 − queueCapacity</c> samples. Devices that do not
/// reset on connect should set it to 0.
/// </para>
/// <para>
/// <b>Threading:</b> <see cref="Send"/> is safe to call concurrently — formatting uses a
/// thread-local buffer and only the enqueue is locked, so two threads can never interleave halves of
/// a line. Exactly one thread ever touches the port — <see cref="TryChangePort"/> is callable from
/// anywhere but only leaves a request behind for that thread to act on. <see cref="IsConnected"/>
/// changes are raised from the writer thread, so a view model must marshal them before binding.
/// </para>
/// <para>
/// <b>Disposal:</b> <see cref="Dispose"/> flushes what is already queued, then closes the port. The
/// flush is bounded — a port that blocks forever cannot hold up application shutdown — and anything
/// still unsent when the deadline passes is counted as dropped.
/// </para>
/// <para>
/// <b>Logging:</b> a <c>Path</c> in the JSON turns on two files, and the pair is the point — each
/// answers a question the other cannot.
/// <list type="bullet">
///   <item><description>
///     <c>&lt;Name&gt;_data.csv</c> — every sample handed to this block, at full precision, written
///     from the publishing thread by <see cref="BaseBlock.Dumper"/>. This is the input record: a
///     sample the queue later discarded still appears here.
///   </description></item>
///   <item><description>
///     <c>&lt;Name&gt;_wire.txt</c> — the bytes that actually left the port, verbatim, written from
///     the writer thread <i>after</i> each successful write. This is the output record: rounded to
///     <c>decimals</c>, delimiters included, and containing nothing that was dropped.
///   </description></item>
/// </list>
/// Diffing the two shows exactly what the link cost, in a way <see cref="GetStats"/> can only total
/// up.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "HandSerial": {
///     "Type": "SerialSender",
///     "Params": [ "COM3", 115200, "\n", ",", 2, 256, 500, 2000, 0.0, true, "DropNewest" ],
///     "Inputs": [ "Predictor" ],
///     "DesiredRate": 50,
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// A sample of <c>[84.3712, -0.5, 0.0]</c> leaves as <c>84.37,-0.50,0.00\n</c>.
/// </para>
/// <para>
/// The <c>Path</c> above writes <c>HandSerial_data.csv</c> and <c>HandSerial_wire.txt</c> into that
/// directory — the sample as it arrived and the line as it left. Omit it and the block transmits
/// exactly the same, logging nothing.
/// </para>
/// <para>
/// <b>Params</b> — positional; an empty string keeps the default for that position:
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description><c>portName</c> (string) — required, e.g. <c>"COM3"</c> or <c>"/dev/ttyUSB0"</c>. The starting port; the card can retarget it while running.</description></item>
///   <item><term>1</term><description><c>baudRate</c> (int) — default 115200. Likewise.</description></item>
///   <item><term>2</term><description><c>terminator</c> (string) — default <c>"\n"</c>.</description></item>
///   <item><term>3</term><description><c>separator</c> (string) — default <c>","</c>.</description></item>
///   <item><term>4</term><description><c>decimals</c> (int) — default 2.</description></item>
///   <item><term>5</term><description><c>queueCapacity</c> (int) — default 256.</description></item>
///   <item><term>6</term><description><c>writeTimeoutMs</c> (int) — default 500.</description></item>
///   <item><term>7</term><description><c>resetDelayMs</c> (int) — default 2000.</description></item>
///   <item><term>8</term><description><c>nonFiniteValue</c> (double) — default 0.0.</description></item>
///   <item><term>9</term><description><c>autoReconnect</c> (bool) — default <c>true</c>.</description></item>
///   <item><term>10</term><description><c>dropPolicy</c> (string) — <c>"DropNewest"</c> (default) or <c>"DropOldest"</c>.</description></item>
/// </list>
/// </para>
/// </example>
public sealed class SerialSender : BaseBlock
{
    #region Constants

    /// <summary>How long the writer parks on an empty queue before re-checking for shutdown.</summary>
    private const int QueueWaitMs = 50;

    /// <summary>Delay between failed open attempts while auto-reconnecting.</summary>
    private const int ReconnectDelayMs = 1000;

    /// <summary>Floor on how long <see cref="Dispose"/> waits for the flush to finish.</summary>
    private const int MinFlushJoinMs = 2000;

    #endregion

    #region Fields

    /// <summary>
    /// Validated configuration in effect right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaced wholesale — never mutated — when <see cref="TryChangePort"/> is applied, and only by
    /// the writer thread. The record is immutable and the reference assignment is atomic, so a reader
    /// on the pipeline thread always sees one coherent configuration; the worst it can see is the
    /// previous one for an instant, which costs at most one line addressed with the old port's name
    /// in a status string.
    /// </para>
    /// <para>
    /// Only <see cref="SerialSenderOptions.PortName"/> and <see cref="SerialSenderOptions.BaudRate"/>
    /// ever change. The framing fields are fixed for the block's lifetime — <see cref="_numberFormat"/>
    /// and <see cref="_queue"/> are derived from them at construction, and altering them mid-stream
    /// would desynchronise the firmware's parser rather than reconfigure it.
    /// </para>
    /// </remarks>
    private SerialSenderOptions _options;

    /// <summary>
    /// The port this block transmits through. Created and owned here.
    /// </summary>
    /// <remarks>
    /// Replaced with a fresh <see cref="SystemSerialPort"/> when a port change is applied, which is
    /// why this is not <c>readonly</c>. Only the writer thread assigns it, and only from
    /// <see cref="ApplyPendingPort"/> — the same thread that is the sole legitimate user of a port, so
    /// the swap can never happen underneath an in-progress write.
    /// </remarks>
    private SystemSerialPort _port;

    /// <summary>Numeric format string, precomputed as <c>"F{decimals}"</c>.</summary>
    private readonly string _numberFormat;

    /// <summary>Outbound lines awaiting the port. Guarded by <see cref="_queueLock"/>.</summary>
    private readonly Queue<byte[]> _queue;

    /// <summary>Guards <see cref="_queue"/> and carries the writer's wake-up pulse.</summary>
    private readonly object _queueLock = new();

    /// <summary>The single thread permitted to touch <see cref="_port"/>.</summary>
    private readonly Thread _writer;

    /// <summary>Set on shutdown to cut short any wait the writer is parked in.</summary>
    private readonly ManualResetEventSlim _stopSignal = new(false);

    /// <summary>
    /// Verbatim log of the bytes that left the port, or <see langword="null"/> when no <c>Path</c>
    /// was configured or the file could not be opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Touched by the writer thread only, which is why it needs no lock and no queue of its own. That
    /// thread is already the slow path — it spends its time blocked in a port write — so one buffered
    /// file write costs nothing that matters, and doing it inline keeps the file's order identical to
    /// the wire's.
    /// </para>
    /// <para>
    /// <see cref="StreamWriter.AutoFlush"/> is on so the file is complete after every line rather
    /// than after a buffer fills. A crash mid-session then still leaves a truthful record, which is
    /// most of the reason to keep this log at all.
    /// </para>
    /// </remarks>
    private readonly StreamWriter? _wireDump;

    /// <summary>
    /// Scratch buffer for line formatting.
    /// </summary>
    /// <remarks>
    /// Thread-static rather than shared: <see cref="Send"/> is documented as concurrency-safe, and a
    /// per-thread builder delivers that without a lock on the formatting path.
    /// </remarks>
    [ThreadStatic] private static StringBuilder? _builder;

    /// <summary>Most recently written line, kept for UI display. Written by the writer thread only.</summary>
    private volatile byte[]? _lastLine;

    /// <summary>Message of the most recent port failure.</summary>
    private volatile string? _lastError;

    /// <summary>Set by <see cref="Dispose"/> to stop the writer once the queue is drained.</summary>
    private volatile bool _stopRequested;

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    private volatile bool _disposed;

    /// <summary>
    /// A validated configuration waiting to be adopted by the writer thread, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// The handoff for <see cref="TryChangePort"/>. Published by whichever thread calls that method —
    /// in practice the UI thread — and claimed by the writer through
    /// <see cref="Interlocked.Exchange{T}(ref T, T)"/>, so the port itself is still only ever touched
    /// by one thread. A second change requested before the first is picked up simply replaces it: the
    /// last choice made is the one that takes effect, which is what a person clicking through a
    /// combo box means.
    /// </remarks>
    private SerialSenderOptions? _pendingOptions;

    /// <summary>Whether an open has been attempted at least once, gating non-reconnecting retries.</summary>
    private bool _openAttempted;

    /// <summary>Whether the port has ever opened, used to tell a reconnect from the first connect.</summary>
    private bool _everConnected;

    /// <summary>
    /// 1 while a dequeued line is being written and has not yet been counted, 0 otherwise.
    /// </summary>
    /// <remarks>
    /// A line in that window belongs to neither the queue nor the counters, which would leave it
    /// unaccounted if disposal abandoned a writer blocked inside a port write. Both the writer and
    /// <see cref="Dispose"/> claim it through <see cref="ClaimInFlight"/>, so exactly one of them
    /// counts it and the totals stay exact either way.
    /// </remarks>
    private int _inFlight;

    // Counters. Written from both the pipeline and writer threads, hence Interlocked throughout.
    private long _enqueued;
    private long _written;
    private long _dropped;
    private long _timeouts;
    private long _errors;
    private long _reconnects;

    /// <summary>
    /// Failures writing <see cref="_wireDump"/>, including a failure to open it.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="_errors"/> on purpose. A dump failure costs no data and says
    /// nothing about the link; folding it into the port's error count would make a full disk look
    /// like a loose cable.
    /// </remarks>
    private long _dumpErrors;

    /// <summary>Backing field for <see cref="IsConnected"/>.</summary>
    private bool _isConnected;

    #endregion

    #region Public Surface

    /// <summary>Visualization helper bound by the card; fed with every sample sent.</summary>
    public BlockVisualization Viz { get; } = new();

    /// <summary>
    /// The validated configuration in effect.
    /// </summary>
    /// <remarks>
    /// Everything here is fixed at construction except the port name and baud rate, which
    /// <see cref="TryChangePort"/> can replace. Re-read it after a change rather than caching it —
    /// the record is swapped, not edited.
    /// </remarks>
    public SerialSenderOptions Options => _options;

    /// <summary>
    /// Requests a switch to a different serial port, baud rate, or both.
    /// </summary>
    /// <param name="portName">
    /// Target port, e.g. <c>"COM12"</c> or <c>"/dev/ttyUSB0"</c>. A bare number is expanded to
    /// <c>COM{n}</c> when the port is opened.
    /// </param>
    /// <param name="baudRate">New baud rate, or <see langword="null"/> to keep the current one.</param>
    /// <param name="error">
    /// On failure, why the request was refused. <see langword="null"/> when this returns
    /// <see langword="true"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the request was accepted and handed to the writer thread. Note what
    /// that does <i>not</i> promise: the new port may still fail to open, exactly as the original one
    /// may have. Watch <see cref="IsConnected"/> for the outcome.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Asynchronous by design.</b> This validates the request and returns; the writer thread picks
    /// it up at the top of its next iteration, closes the current port, and lets the ordinary
    /// open path take over — reset delay, reconnect back-off and status events all included. Doing
    /// the close inline would mean a UI click could block on a port stuck in a write.
    /// </para>
    /// <para>
    /// <b>Cost.</b> Samples keep arriving while the switch happens, so the queue's drop policy applies
    /// throughout and a change is visible as a step in <c>Dropped</c> — at <i>R</i> Hz roughly
    /// <c>R × resetDelayMs / 1000 − queueCapacity</c> samples, the same arithmetic as the first
    /// connect. Nothing already queued is discarded on purpose: whatever fits is sent to the new port.
    /// </para>
    /// <para>
    /// <b>Allowed while connected.</b> Deliberately: the case this exists for is a port that opened
    /// perfectly well and turned out to be the wrong device, which is precisely the case a
    /// "disconnect first" guard would lock out.
    /// </para>
    /// </remarks>
    public bool TryChangePort(string portName, int? baudRate, out string? error)
    {
        if (_disposed)
        {
            error = "Block is disposed.";
            return false;
        }

        var current = _options;

        try
        {
            // Validated through the same path as a JSON config, so a port typed into the UI cannot
            // reach the writer in a state the constructor would have rejected.
            var candidate = (current with
            {
                PortName = portName,
                BaudRate = baudRate ?? current.BaudRate
            }).Normalized();

            Interlocked.Exchange(ref _pendingOptions, candidate);

            // The writer may be parked on an empty queue for up to QueueWaitMs. Waking it makes the
            // switch feel immediate rather than arriving up to a tick late.
            lock (_queueLock) Monitor.Pulse(_queueLock);

            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Whether the port is currently open.
    /// </summary>
    /// <remarks>
    /// Maintained by the writer thread, so <c>PropertyChanged</c> is raised off the UI thread and a
    /// view model must marshal it.
    /// </remarks>
    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    /// <summary>Lines currently queued and not yet handed to the port.</summary>
    public int QueueDepth
    {
        get { lock (_queueLock) return _queue.Count; }
    }

    /// <summary>
    /// The last line written, with its terminator trimmed, or <see langword="null"/> if none.
    /// </summary>
    /// <remarks>
    /// Decoded on read rather than on write, so displaying it costs the UI thread and not the hot
    /// path.
    /// </remarks>
    public string? LastLine
    {
        get
        {
            var line = _lastLine;
            return line is null ? null : Encoding.ASCII.GetString(line).TrimEnd('\r', '\n');
        }
    }

    /// <summary>Raised on connect, disconnect and port failure. Fired from the writer thread.</summary>
    public event EventHandler<string>? StatusChanged;

    #endregion

    #region Construction / Factory

    /// <summary>
    /// Initializes a new <see cref="SerialSender"/> and starts its writer thread.
    /// </summary>
    /// <param name="options">Configuration. Validated here; this is the only place that throws.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any configuration value is invalid.</exception>
    /// <remarks>
    /// Hardware trouble is not a construction failure. If the port cannot be opened the block starts
    /// disconnected and retries on the writer thread, because a pipeline that refuses to load because
    /// a USB cable is loose is worse than one that reports a disconnected block.
    /// </remarks>
    public SerialSender(SerialSenderOptions options)
        : this(Validate(options), alreadyValidated: true)
    {
    }

    /// <summary>
    /// Real constructor, reached only with options already validated.
    /// </summary>
    /// <param name="options">Options that have already been through <see cref="Validate"/>.</param>
    /// <param name="alreadyValidated">
    /// Never read. It exists to give this constructor a signature distinct from the public one.
    /// </param>
    /// <remarks>
    /// Exists so that validation runs exactly once, in the argument list of the <c>this(...)</c> call
    /// above — <see cref="BaseBlock"/>'s constructor has to be handed a name and rate that have
    /// already survived checking, and it runs before any statement in a body could do the checking.
    /// </remarks>
    private SerialSender(SerialSenderOptions options, bool alreadyValidated)
        : base(options.Name, options.DesiredRate)
    {
        _options = options;
        _port = new SystemSerialPort(options.PortName, options.BaudRate);
        _numberFormat = "F" + options.Decimals.ToString(CultureInfo.InvariantCulture);
        _queue = new Queue<byte[]>(options.QueueCapacity);

        // Opened before the writer starts, so no line can be transmitted before the file that is
        // supposed to witness it exists.
        _wireDump = OpenWireDump(options);

        _writer = new Thread(WriterLoop)
        {
            // Background so an abandoned writer — one still blocked in a port write when the flush
            // deadline passed — can never keep the process alive.
            IsBackground = true,
            Name = $"SerialSender:{options.Name}",
            Priority = ThreadPriority.AboveNormal
        };

        _writer.Start();

        Debug.WriteLine($"[{Name}] Serial output to {options.PortName} @ {options.BaudRate} baud, " +
                        $"format F{options.Decimals}, queue {options.QueueCapacity} ({options.DropPolicy})");
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_data.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_data";

    /// <summary>
    /// Creates a <see cref="SerialSender"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider, used only to resolve the CSV dumper.</param>
    /// <param name="m">JSON model. See the class-level example for the <c>Params</c> layout.</param>
    /// <returns>A configured, running <see cref="SerialSender"/>.</returns>
    /// <exception cref="ArgumentException">Any configuration value is invalid.</exception>
    public static SerialSender ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "SerialSender";
        var rate = m.DesiredRate ?? 0;

        // Path drives both logs. It reaches the wire dump through the options record because that
        // dump is opened in the constructor; the CSV dumper is attached afterwards, as every block
        // attaches it.
        var options = SerialSenderOptions.FromParams(m.Params, name, rate) with { DumpPath = m.Path };

        // Constructed directly rather than through ActivatorUtilities: SerialSenderOptions is not a
        // registered service, and ActivatorUtilities cannot match a parameter it has neither a
        // service for nor an argument to bind.
        var block = new SerialSender(options);

        return block;
    }

    /// <summary>Null-checks and validates options on the way into the constructor chain.</summary>
    private static SerialSenderOptions Validate(SerialSenderOptions options)
        => (options ?? throw new ArgumentNullException(nameof(options))).Normalized();

    /// <summary>
    /// Opens <c>&lt;Name&gt;_wire.txt</c> for the session, or returns <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FileMode.Create"/> truncates, so a file always describes exactly one run. Appending
    /// would blur session boundaries in a log whose whole value is being able to say "this is what
    /// the board received, in order, this time".
    /// </para>
    /// <para>
    /// <see cref="FileShare.Read"/> lets the file be tailed while the pipeline runs. Combined with
    /// autoflush, that makes it a live view of the link rather than a post-mortem.
    /// </para>
    /// <para>
    /// Never throws. The constructor is the only place allowed to, and it is allowed to for bad
    /// <i>configuration</i> — a directory that cannot be written is not that, and must not take a
    /// working serial link down with it.
    /// </para>
    /// </remarks>
    private StreamWriter? OpenWireDump(SerialSenderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DumpPath)) return null;

        try
        {
            Directory.CreateDirectory(options.DumpPath);

            var path = Path.Combine(options.DumpPath, $"{options.Name}_wire.txt");
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);

            return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _dumpErrors);
            Debug.WriteLine($"[{options.Name}] Wire dump disabled — could not open it: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "SerialSender";

    /// <inheritdoc />
    /// <remarks>
    /// Emits every position so a round-tripped pipeline is explicit about its configuration rather
    /// than inheriting whatever the defaults happen to be next release. Delimiters are written
    /// unescaped — the JSON writer escapes them again on the way out.
    /// <para>
    /// Reads the live options, so a port chosen on the card is what gets saved. Picking the right
    /// board and then saving the pipeline is how that choice becomes permanent — nothing is written
    /// back to the source file on its own.
    /// </para>
    /// </remarks>
    protected override IReadOnlyList<object>? GetJsonParams() => new List<object>
    {
        _options.PortName,
        _options.BaudRate,
        _options.Terminator,
        _options.Separator,
        _options.Decimals,
        _options.QueueCapacity,
        _options.WriteTimeoutMs,
        _options.ResetDelayMs,
        _options.NonFiniteValue,
        _options.AutoReconnect,
        _options.DropPolicy.ToString()
    };

    #endregion

    #region Hot Path

    /// <summary>
    /// Formats a sample, queues it for transmission, and publishes it downstream.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="data">
    /// A <see cref="Vector{T}"/> of <see cref="double"/>, a <c>double[]</c>, or a single
    /// <see cref="double"/>. Anything else is logged and ignored.
    /// </param>
    /// <remarks>
    /// The sample is published even when the port is down, so downstream blocks and the CSV dumper
    /// see a continuous signal regardless of the link's state.
    /// </remarks>
    protected override void OnReceive(object sender, object data)
    {
        if (_disposed || data is null) return;

        var vector = data as Vector<double>;

        // AsArray hands back the vector's own storage when it is dense — the common case — so the
        // hot path copies nothing.
        var sample = vector is not null
            ? vector.AsArray() ?? vector.ToArray()
            : data as double[];

        if (sample is null && data is double scalar)
            sample = new[] { scalar };

        if (sample is null)
        {
            Debug.WriteLine($"[{Name}] Expected Vector<double>, double[] or double, got {data.GetType().Name}");
            return;
        }

        Send(sample);

        Publish(vector ?? Vector<double>.Build.Dense(sample));
        Viz.Feed(sample);
    }

    /// <summary>
    /// Formats one sample and queues it for the writer thread.
    /// </summary>
    /// <param name="sample">
    /// Field values, in wire order. An empty span is legal and emits a bare terminator, which is how
    /// a heartbeat or an end-of-record marker is sent.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the line was queued, <see langword="false"/> if it was dropped
    /// because the queue was full or the block is shutting down.
    /// </returns>
    /// <remarks>
    /// Never blocks on the port: formatting is thread-local and the only lock held is the queue's,
    /// for the duration of one enqueue. A port stuck in a write does not slow this down.
    /// </remarks>
    public bool Send(ReadOnlySpan<double> sample)
    {
        if (_disposed) return false;

        return Enqueue(FormatLine(sample));
    }

    /// <summary>
    /// Renders one sample as ASCII: fields joined by the separator, closed by the terminator.
    /// </summary>
    /// <remarks>
    /// Every byte produced here is ASCII by construction — the invariant-culture <c>"F"</c> format
    /// emits only digits, <c>'-'</c> and <c>'.'</c>, and both delimiters were checked for ASCII
    /// during validation — so the encoding step cannot substitute a replacement character.
    /// </remarks>
    private byte[] FormatLine(ReadOnlySpan<double> sample)
    {
        var sb = _builder ??= new StringBuilder(128);
        sb.Clear();

        for (var i = 0; i < sample.Length; i++)
        {
            if (i > 0) sb.Append(_options.Separator);

            var value = sample[i];
            if (double.IsNaN(value) || double.IsInfinity(value)) value = _options.NonFiniteValue;

            sb.Append(value.ToString(_numberFormat, CultureInfo.InvariantCulture));
        }

        sb.Append(_options.Terminator);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Appends a formatted line to the queue, applying the drop policy when it is full.
    /// </summary>
    /// <returns><see langword="true"/> if the line was queued.</returns>
    private bool Enqueue(byte[] line)
    {
        lock (_queueLock)
        {
            Interlocked.Increment(ref _enqueued);

            if (_stopRequested)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            if (_queue.Count >= _options.QueueCapacity)
            {
                if (_options.DropPolicy == DropPolicy.DropNewest)
                {
                    Interlocked.Increment(ref _dropped);
                    return false;
                }

                // DropOldest: evict the stalest line so the newest one still fits.
                _queue.Dequeue();
                Interlocked.Increment(ref _dropped);
            }

            _queue.Enqueue(line);
            Monitor.Pulse(_queueLock);
            return true;
        }
    }

    #endregion

    #region Writer Thread

    /// <summary>
    /// Drains the queue to the port for the block's lifetime, reopening as needed.
    /// </summary>
    /// <remarks>
    /// The loop owns the port exclusively. On a stop request it finishes whatever is already queued
    /// and exits; it does not attempt to open a closed port to flush, because waiting out a reset
    /// delay during shutdown would be worse than losing the backlog.
    /// </remarks>
    private void WriterLoop()
    {
        try
        {
            while (true)
            {
                var stopping = _stopRequested;

                // Before the open check, so a swapped-in port is seen closed and goes straight down
                // the ordinary connect path. Skipped while stopping: a shutdown flush belongs to the
                // port that holds the backlog, not to one nobody has connected to yet.
                if (!stopping) ApplyPendingPort();

                if (!_port.IsOpen)
                {
                    if (stopping) break;
                    if (!TryOpen()) continue;
                }

                // While stopping, an empty queue means the flush is done.
                var line = TryDequeue(stopping ? 0 : QueueWaitMs);
                if (line is null)
                {
                    if (stopping) break;
                    continue;
                }

                WriteToPort(line);
            }
        }
        catch (Exception ex)
        {
            // Reaching here means a bug, or a race with disposal on an abandoned thread. Either way
            // a background thread must not take the process down on its way out.
            Debug.WriteLine($"[{Name}] Writer thread terminated: {ex}");
        }
        finally
        {
            SetConnected(false);
        }
    }

    /// <summary>
    /// Takes the next queued line, waiting up to <paramref name="waitMs"/> for one to arrive.
    /// </summary>
    /// <returns>The next line, or <see langword="null"/> if none arrived in time.</returns>
    /// <remarks>
    /// Marks the line in-flight while still holding the lock, so it is never invisible to both the
    /// queue and <see cref="_inFlight"/> at the same time.
    /// </remarks>
    private byte[]? TryDequeue(int waitMs)
    {
        lock (_queueLock)
        {
            if (_queue.Count == 0 && waitMs > 0 && !_stopRequested)
                Monitor.Wait(_queueLock, waitMs);

            if (_queue.Count == 0) return null;

            var line = _queue.Dequeue();
            Volatile.Write(ref _inFlight, 1);
            return line;
        }
    }

    /// <summary>
    /// Adopts a pending port change, if one was requested since the last iteration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on the writer thread, which is what makes the swap safe without a lock on the port: the
    /// only thread that could be mid-write is the one executing this, and it is plainly not.
    /// </para>
    /// <para>
    /// A fresh <see cref="SystemSerialPort"/> rather than a reconfigured one — that wrapper caches its
    /// name and rate for the reopen strategy documented on it, and building a new one keeps that
    /// arrangement honest instead of introducing a second way to change them.
    /// </para>
    /// <para>
    /// <see cref="_openAttempted"/> is cleared so that a block configured with
    /// <c>autoReconnect: false</c>, having already spent its single attempt, still gets one for the
    /// port just chosen. Asking for a different port is an explicit request to try again;
    /// <see cref="_everConnected"/> is left alone, so that attempt is counted as a reconnect.
    /// </para>
    /// </remarks>
    private void ApplyPendingPort()
    {
        var pending = Interlocked.Exchange(ref _pendingOptions, null);
        if (pending is null) return;

        var previous = _port;

        SafeClose();
        SetConnected(false);

        _port = new SystemSerialPort(pending.PortName, pending.BaudRate);
        Volatile.Write(ref _options, pending);

        try { previous.Dispose(); } catch { /* the device may already be gone */ }

        _openAttempted = false;

        // The old port's last failure describes hardware nobody is talking to any more; leaving it on
        // the card would blame the new port for it.
        _lastError = null;

        Debug.WriteLine($"[{Name}] Switching to {pending.PortName} @ {pending.BaudRate} baud");
        RaiseStatus($"Switching to {pending.PortName} @ {pending.BaudRate} baud");
    }

    /// <summary>
    /// Attempts to open the port, waiting out the reset delay on success and backing off on failure.
    /// </summary>
    /// <returns><see langword="true"/> if the port is open and settled.</returns>
    /// <remarks>
    /// Every wait goes through <see cref="_stopSignal"/> rather than <c>Thread.Sleep</c>, so disposal
    /// is not held hostage by a two-second reset delay or a reconnect back-off.
    /// </remarks>
    private bool TryOpen()
    {
        if (_openAttempted && !_options.AutoReconnect)
        {
            // One shot was all that was asked for. Idle here; the queue keeps applying its policy.
            _stopSignal.Wait(QueueWaitMs);
            return false;
        }

        _openAttempted = true;

        try
        {
            _port.DtrEnable = true;
            _port.WriteTimeout = _options.WriteTimeoutMs;
            _port.Open();

            if (_everConnected) Interlocked.Increment(ref _reconnects);
            _everConnected = true;

            SetConnected(true);
            RaiseStatus($"Connected to {_options.PortName} @ {_options.BaudRate} baud");

            if (_options.ResetDelayMs > 0) _stopSignal.Wait(_options.ResetDelayMs);
            return true;
        }
        catch (Exception ex)
        {
            RecordError(ex);
            SafeClose();
            SetConnected(false);

            if (!_options.AutoReconnect) return false;

            _stopSignal.Wait(ReconnectDelayMs);
            return false;
        }
    }

    /// <summary>
    /// Writes one line, classifying any failure as either a stalled peer or a broken link.
    /// </summary>
    /// <remarks>
    /// A timeout leaves the port open. The receiver is not draining its buffer, which is a condition
    /// that clears on its own; closing would convert a slow consumer into a reconnect storm and lose
    /// far more than the one sample. Anything else means the link itself is gone, so the port is
    /// closed and the loop reopens it.
    /// </remarks>
    private void WriteToPort(byte[] line)
    {
        try
        {
            _port.Write(line, 0, line.Length);

            // After the write, never before: a line in the file is a line the port accepted. Logging
            // on the way in would record intent, and intent is exactly what a dropped or timed-out
            // write disproves.
            DumpWire(line);

            _lastLine = line;

            if (ClaimInFlight()) Interlocked.Increment(ref _written);
        }
        catch (TimeoutException ex)
        {
            if (ClaimInFlight())
            {
                Interlocked.Increment(ref _timeouts);
                Interlocked.Increment(ref _dropped);
            }

            RecordError(ex);
        }
        catch (Exception ex)
        {
            if (ClaimInFlight()) Interlocked.Increment(ref _dropped);

            RecordError(ex);
            SafeClose();
            SetConnected(false);
        }
    }

    /// <summary>
    /// Takes ownership of counting the line currently in flight.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> for the first caller, <see langword="false"/> for any other — so a
    /// writer that returns after <see cref="Dispose"/> already wrote the line off does not count it
    /// a second time.
    /// </returns>
    private bool ClaimInFlight() => Interlocked.Exchange(ref _inFlight, 0) == 1;

    /// <summary>
    /// Appends one transmitted line to the wire dump, verbatim.
    /// </summary>
    /// <param name="line">The buffer just handed to the port.</param>
    /// <remarks>
    /// <para>
    /// Decoded from the same bytes the port received rather than from the string they were built
    /// from, so the file shows what the wire carried and not what was meant to be on it.
    /// </para>
    /// <para>
    /// Written with <see cref="StreamWriter.Write(string)"/>, not <c>WriteLine</c>: the terminator is
    /// already part of the line. Adding one would put a byte in the file that never left the port,
    /// which is the one thing this log must not do — and would silently break a config whose
    /// terminator is <c>\r</c> or something else entirely.
    /// </para>
    /// <para>
    /// Its own try/catch, deliberately nested inside the caller's. Letting a disk error escape here
    /// would reach the link-failure handler in <see cref="WriteToPort"/>, which would close a
    /// perfectly good port because a log file filled up.
    /// </para>
    /// </remarks>
    private void DumpWire(byte[] line)
    {
        var dump = _wireDump;
        if (dump is null) return;

        try
        {
            dump.Write(Encoding.ASCII.GetString(line));
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _dumpErrors);
            Debug.WriteLine($"[{Name}] Wire dump write failed: {ex.Message}");
        }
    }

    /// <summary>Closes the port, ignoring failures from a device that has already vanished.</summary>
    private void SafeClose()
    {
        try { _port.Close(); } catch { /* the device may already be gone */ }
    }

    /// <summary>Records a failure for <see cref="GetStats"/> and notifies listeners.</summary>
    private void RecordError(Exception ex)
    {
        Interlocked.Increment(ref _errors);
        _lastError = ex.Message;
        Debug.WriteLine($"[{Name}] {ex.GetType().Name}: {ex.Message}");
        RaiseStatus($"Error: {ex.Message}");
    }

    /// <summary>Updates <see cref="IsConnected"/> and announces the transition once.</summary>
    private void SetConnected(bool connected)
    {
        if (_isConnected == connected) return;

        IsConnected = connected;
        if (!connected) RaiseStatus($"Disconnected from {_options.PortName}");
    }

    /// <summary>Raises <see cref="StatusChanged"/>, absorbing anything a handler throws.</summary>
    /// <remarks>
    /// A subscriber's exception would otherwise unwind the writer loop and stop transmission
    /// entirely — too steep a price for a misbehaving view model.
    /// </remarks>
    private void RaiseStatus(string status)
    {
        try { StatusChanged?.Invoke(this, status); }
        catch (Exception ex) { Debug.WriteLine($"[{Name}] StatusChanged handler threw: {ex.Message}"); }
    }

    #endregion

    #region Diagnostics

    /// <summary>
    /// Samples the traffic counters.
    /// </summary>
    /// <returns>A consistent snapshot; see <see cref="SerialSenderStats"/> for the invariant it upholds.</returns>
    /// <remarks>
    /// Cheap enough to poll from a UI timer, which is the intended use — the send path raises no
    /// per-line events, precisely so that a 1 kHz stream does not turn into 1 000 UI notifications a
    /// second.
    /// </remarks>
    public SerialSenderStats GetStats()
    {
        int pending;
        lock (_queueLock) pending = _queue.Count;

        return new SerialSenderStats(
            Enqueued: Interlocked.Read(ref _enqueued),
            Written: Interlocked.Read(ref _written),
            Dropped: Interlocked.Read(ref _dropped),
            Timeouts: Interlocked.Read(ref _timeouts),
            Errors: Interlocked.Read(ref _errors),
            DumpErrors: Interlocked.Read(ref _dumpErrors),
            Reconnects: Interlocked.Read(ref _reconnects),
            Pending: pending,
            IsConnected: _isConnected,
            LastError: _lastError);
    }

    #endregion

    #region Lifecycle

    /// <summary>
    /// Flushes the queue, stops the writer thread, and releases the port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The flush is bounded. If the writer is stuck in a port write that never returns, waiting for
    /// it would hang shutdown, so it is abandoned after the deadline — safely, since it is a
    /// background thread that catches everything it might hit on the way out. Whatever it never got
    /// to is counted as dropped, keeping <c>Enqueued == Written + Dropped</c> exact once this
    /// returns.
    /// </para>
    /// <para>
    /// <see cref="_stopSignal"/> and <see cref="_wireDump"/> are only disposed when the thread
    /// actually finished; leaving them alive for an abandoned writer costs two handles and avoids an
    /// <see cref="ObjectDisposedException"/> on a thread nobody is watching. The dump loses nothing
    /// by staying open — autoflush already put every line on disk.
    /// </para>
    /// </remarks>
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_queueLock)
        {
            _stopRequested = true;
            Monitor.PulseAll(_queueLock);
        }

        _stopSignal.Set();

        var joinMs = Math.Max(MinFlushJoinMs, _options.WriteTimeoutMs * 2);
        var finished = _writer.Join(joinMs);

        if (!finished)
            Debug.WriteLine($"[{Name}] Writer did not finish within {joinMs} ms; abandoning it.");

        int abandoned;
        lock (_queueLock)
        {
            abandoned = _queue.Count;
            _queue.Clear();
        }

        // Claims the line the abandoned writer is still blocked on, if there is one. When the thread
        // finished cleanly it has already claimed its own and this is a no-op.
        if (ClaimInFlight()) abandoned++;

        if (abandoned > 0)
        {
            Interlocked.Add(ref _dropped, abandoned);
            Debug.WriteLine($"[{Name}] Dropped {abandoned} unsent line(s) at shutdown.");
        }

        SafeClose();
        _port.Dispose();
        if (finished) _stopSignal.Dispose();

        // Only once the writer has actually stopped — it is the sole user of this handle, and closing
        // it under an abandoned thread would turn its next successful write into an unhandled
        // ObjectDisposedException on a background thread. Autoflush means nothing is lost by leaving
        // it open in that case: every line already reached disk when it was written.
        if (finished)
        {
            try { _wireDump?.Dispose(); }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _dumpErrors);
                Debug.WriteLine($"[{Name}] Wire dump close failed: {ex.Message}");
            }
        }

        Viz.Dispose();
        base.Dispose();
    }

    #endregion
}
