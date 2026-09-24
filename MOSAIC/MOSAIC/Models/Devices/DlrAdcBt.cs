using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.IO;
using MOSAIC.Diagnostics;
using MOSAIC.Visualization;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.Models.Devices;

/// <summary>
/// Acquires analogue samples from a DLR ADC/BT board over a Bluetooth SPP serial port.
/// </summary>
/// <remarks>
/// <para>
/// <b>Clock driven.</b> The board free-runs once told to stream: a background reader thread
/// (<see cref="StreamManager"/>) accumulates bytes continuously, and each tick of the upstream
/// <c>Clock</c> takes the frame at the head of the buffer. The block therefore publishes at
/// exactly the pipeline rate rather than at whatever rate the radio happens to deliver. When no
/// complete frame is available on a tick the previous sample is republished (sample-and-hold),
/// so the downstream rate never stutters.
/// </para>
/// <para>
/// <b>Wire format.</b> Every frame is <c>4 + 2 * channels + 4</c> bytes:
/// </para>
/// <list type="table">
///   <listheader><term>Offset</term><description>Contents</description></listheader>
///   <item><term>0..1</term><description>Start marker <c>0xAA 0x7A</c>.</description></item>
///   <item><term>2</term><description>Frame length in bytes - must equal the expected frame size.</description></item>
///   <item><term>3</term><description>Command echo (unused).</description></item>
///   <item><term>4..</term><description>One big-endian <c>uint16</c> per channel.</description></item>
///   <item><term>last 4</term><description>Trailer, ending in <c>0xFF 0xFF</c>.</description></item>
/// </list>
/// <para>
/// Counts are converted to volts as <c>5.0 * raw / 4000.0</c>, the conversion the original
/// iM-Blocks driver used.
/// </para>
/// <para>
/// <b>Output:</b> a <see cref="Vector"/> of <see cref="NumOfChannels"/> volts, one element
/// per ADC channel.
/// </para>
/// </remarks>
/// <example>
/// <para>JSON pipeline configuration:</para>
/// <code language="json">
/// {
///   "Name": "DlrAdc",
///   "Type": "DlrAdcBt",
///   "Inputs": [ "Timer100Hz" ],
///   "Params": [ "COM7", 10, 115200 ]
/// }
/// </code>
/// </example>
public sealed partial class DlrAdcBt : BaseBlock
{
    #region Protocol Constants

    private const byte FrameStart0 = 0xAA;
    private const byte FrameStart1 = 0x7A;
    private const byte FrameEnd    = 0xFF;

    private const int HeaderBytes     = 4;   // start marker, length, command echo
    private const int TrailerBytes    = 4;   // two spare bytes then 0xFF 0xFF
    private const int BytesPerChannel = 2;   // big-endian uint16

    /// <summary>ADC reference voltage.</summary>
    private const double ReferenceVolts = 5.0;

    /// <summary>Full-scale count the firmware maps <see cref="ReferenceVolts"/> onto.</summary>
    private const double FullScaleCounts = 4000.0;

    /// <summary>The channel count travels in a single command byte, so it cannot exceed 255.</summary>
    private const int MaxChannels = 255;

    /// <summary>How many frames the receive buffer holds before the oldest bytes are discarded.</summary>
    private const int RxBufferFrames = 16;

    // Byte 4 carries the channel count and is patched in Connect(); the rest is a fixed preamble.
    private readonly byte[] _cmdNumOfChannels =
    [
        0xAA, 0x7A, 0x03, 0x01, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x8F, 0xFF, 0xEF
    ];

    private readonly byte[] _cmdStartStreaming =
    [
        0xAA, 0x7A, 0x01, 0x6A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x8F, 0xFF, 0xEF
    ];

    private readonly byte[] _cmdStopStreaming =
    [
        0xAA, 0x7A, 0x01, 0x6B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x8F, 0xFF, 0xEF
    ];

    #endregion

    #region Internal State

    private SerialPort?    _serialPort;
    private StreamManager? _streamManager;

    // Guards _rxBuffer / _rxCount, written by the reader thread and read by the clocked
    // parse path. A dedicated lock object: the buffer array itself is replaced whenever the
    // channel count changes, so locking on it would swap the monitor out from under a waiter.
    private readonly object _bufferLock = new();
    private byte[] _rxBuffer = [];
    private int    _rxCount;

    private string   _portNumber    = string.Empty;
    private int      _numOfChannels = 10;
    private double[] _sample        = new double[10];
    private int      _bytesPerFrame;
    private bool     _disposed;

    #endregion

    #region Configuration

    /// <summary>Serial port name of the Bluetooth SPP outbound port (e.g. "COM7").</summary>
    public string PortNumber
    {
        get => _portNumber;
        set
        {
            // Accept bare numeric ports for compatibility with existing pipeline files.
            var trimmed = value?.Trim() ?? string.Empty;
            _portNumber = int.TryParse(trimmed, out _) ? $"COM{trimmed}" : trimmed;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Number of ADC channels to request from the board. Assigning a new value resizes the
    /// output sample, so the published vector can never disagree with the configured width.
    /// </summary>
    /// <remarks>
    /// Takes effect on the next <see cref="Connect"/> - the board is told its frame width once,
    /// at the start of a streaming session.
    /// </remarks>
    public int NumOfChannels
    {
        get => _numOfChannels;
        set
        {
            int clamped = Math.Clamp(value, 1, MaxChannels);
            if (clamped == _numOfChannels && _sample.Length == clamped) return;
            _numOfChannels = clamped;
            ResetBuffers();
            OnPropertyChanged();
        }
    }

    /// <summary>Serial baud rate. The board ships at 115200.</summary>
    [ObservableProperty] private int _baudRate = 115200;

    /// <summary>Whether the serial port is currently open.</summary>
    [ObservableProperty] private bool _isConnected;

    /// <summary>Whether the board has been told to stream and the reader thread is running.</summary>
    [ObservableProperty] private bool _isStreaming;

    /// <summary>
    /// Whether the port is genuinely open, read from the port itself.
    /// </summary>
    /// <remarks>
    /// <see cref="Connect"/> records a failure in <see cref="Info"/> and returns normally rather
    /// than throwing, so a caller that only watches for an exception can believe it is connected
    /// over a port that never opened. This is the truth.
    /// </remarks>
    public bool IsPortOpen => _serialPort is { IsOpen: true };

    /// <summary>Frame size in bytes for the configured channel count.</summary>
    public int BytesPerFrame => HeaderBytes + BytesPerChannel * NumOfChannels + TrailerBytes;

    /// <summary>Observable status information for the card UI.</summary>
    public DlrAdcBtInfo Info { get; } = new();

    /// <summary>Live plot of the published sample, shown on the device card.</summary>
    public BlockVisualization Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>COM ports currently present on the machine, for the card's port picker.</summary>
    public static string[] AvailablePorts => SerialPort.GetPortNames();

    /// <summary>Re-enumerates the COM ports. Called from the ViewModel.</summary>
    public static string[] RefreshPorts() => SerialPort.GetPortNames();

    #endregion

    #region Input Constraints

    /// <inheritdoc />
    public override int MinInputs => 1;

    /// <inheritdoc />
    public override int MaxInputs => 1;

    /// <summary>
    /// The block is polled, not pushed: it needs a clock to decide when a frame is taken from
    /// the reader's buffer, and nothing else it could be fed would mean anything.
    /// </summary>
    public override string[] AllowableBlocks => ["ClockBlock"];

    #endregion

    #region Construction / Factory

    /// <summary>
    /// Initialises a new <see cref="DlrAdcBt"/> block.
    /// </summary>
    /// <param name="name">Display name for this block.</param>
    /// <param name="desiredRate">Sampling rate in Hz. 0 inherits the upstream clock's rate.</param>
    public DlrAdcBt(string name = "DlrAdcBt", double desiredRate = 0)
        : base(name, desiredRate)
    {
        ResetBuffers();
    }

    /// <summary>
    /// (Re)allocates the receive buffer and the output sample for the current channel count, and
    /// discards anything buffered.
    /// </summary>
    /// <remarks>
    /// Sized here rather than in <see cref="Connect"/> so the frame width is a function of the
    /// configured channel count alone. A buffer that only existed after a successful port open
    /// would make the parser's behaviour depend on hardware being present.
    /// </remarks>
    private void ResetBuffers()
    {
        lock (_bufferLock)
        {
            _bytesPerFrame = HeaderBytes + BytesPerChannel * _numOfChannels + TrailerBytes;
            _sample   = new double[_numOfChannels];
            _rxBuffer = new byte[_bytesPerFrame * RxBufferFrames];
            _rxCount  = 0;
        }
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_out.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_out";

    /// <summary>
    /// Creates a <see cref="DlrAdcBt"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">The application's dependency injection service provider.</param>
    /// <param name="m">
    /// JSON model. <c>Params</c> are positional and all optional:
    /// <c>[portName, numChannels, baudRate]</c>.
    /// </param>
    public static DlrAdcBt ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "DlrAdcBt";
        var rate = m.DesiredRate ?? 0;

        var block = ActivatorUtilities.CreateInstance<DlrAdcBt>(sp, name, rate);

        // Params arrive as JsonElement from the pipeline loader, so they go through the model's
        // own extractors rather than ToString() — that way "COM7", 7 and "7" all mean the port.
        var ps = m.Params;
        if (ps is { Count: > 0 })
            block.PortNumber = JsonModel.GetString(ps[0], string.Empty)!;

        if (ps is { Count: > 1 })
            block.NumOfChannels = JsonModel.GetInt(ps[1], block.NumOfChannels);

        if (ps is { Count: > 2 })
            block.BaudRate = JsonModel.GetInt(ps[2], block.BaudRate);

        return block;
    }

    #endregion

    #region Serial Connection

    /// <summary>
    /// Opens the serial port, tells the board its frame width, and starts streaming.
    /// </summary>
    /// <remarks>
    /// Failures are reported through <see cref="Info"/> rather than thrown, so a bad port
    /// cannot tear down the pipeline that owns this block.
    /// </remarks>
    public void Connect()
    {
        if (IsPortOpen) return;

        if (string.IsNullOrWhiteSpace(PortNumber))
        {
            Info.Flag = "No port configured";
            return;
        }

        _cmdNumOfChannels[4] = (byte)NumOfChannels;
        ResetBuffers();

        try
        {
            // No Encoding is set: unlike the original driver this block reads and writes bytes
            // through BaseStream, so the port's text codec never sees the binary frames.
            _serialPort = new SerialPort(PortNumber, BaudRate, Parity.None, 8, StopBits.One)
            {
                ReceivedBytesThreshold = _bytesPerFrame,
                ReadBufferSize         = 100 * _bytesPerFrame,
                // Finite, unlike the original's -1: StreamManager treats a timeout as "no data
                // yet" and loops, and an infinite read would leave the reader thread wedged in
                // Read() where StopStreaming's Join could never collect it.
                ReadTimeout            = 10,
                WriteBufferSize        = 2048,
                WriteTimeout           = 500
            };
            _serialPort.Open();
        }
        catch (Exception ex)
        {
            Info.Flag = $"Connection failed: {ex.Message}";
            Console.WriteLine($"[{Name}] {Info.Flag}");
            _serialPort?.Dispose();
            _serialPort = null;
            return;
        }

        IsConnected = true;
        Info.Flag = "Connected";
        Console.WriteLine($"[{Name}] Connected on {PortNumber} @ {BaudRate} ({_bytesPerFrame} B/frame)");

        StartStream();
    }

    /// <summary>Stops streaming and closes the serial port.</summary>
    public void Disconnect()
    {
        StopStream();

        try { _serialPort?.Close(); _serialPort?.Dispose(); }
        catch (Exception ex) { Log.Error("DlrAdcBt", Name, ex, "Port close failed."); }

        _serialPort = null;
        IsConnected = false;

        lock (_bufferLock) _rxCount = 0;

        Info.Flag = "Disconnected";
        Console.WriteLine($"[{Name}] Disconnected.");
    }

    /// <summary>Closes and reopens the port, re-sending the configuration commands.</summary>
    public void Reconnect()
    {
        Disconnect();
        Connect();
    }

    /// <summary>
    /// Starts the reader thread, then configures and starts the board's stream.
    /// </summary>
    private void StartStream()
    {
        if (_serialPort is not { IsOpen: true }) return;

        _streamManager = new StreamManager(_serialPort.BaseStream, _bytesPerFrame, 0);
        _streamManager.DataReceived += OnSerialDataReceived;
        _streamManager.StartStreaming();

        try
        {
            // Channel count first: the board sizes its frames from it, so sending it after the
            // start command would leave the opening frames at the previous width.
            _streamManager.Write(_cmdNumOfChannels);
            _streamManager.Write(_cmdStartStreaming);
        }
        catch (Exception ex)
        {
            Info.Flag = $"Start failed: {ex.Message}";
            Console.WriteLine($"[{Name}] {Info.Flag}");
            return;
        }

        IsStreaming = true;
        Info.Flag = "Streaming";

        // One frame is consumed per clock tick, so the tick rate is the true sample rate of the
        // signal this block emits. Downstream DSP designs its coefficients from it.
        if (DesiredRate > 0) SignalRate = DesiredRate;
    }

    /// <summary>
    /// Tells the board to stop, then shuts the reader thread down.
    /// </summary>
    private void StopStream()
    {
        // The original driver declared this command and never sent it, so a disconnected board
        // kept transmitting into a closed port until it was power-cycled.
        try
        {
            if (_serialPort is { IsOpen: true })
                _serialPort.Write(_cmdStopStreaming, 0, _cmdStopStreaming.Length);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Stop command failed: {ex.Message}");
        }

        if (_streamManager is not null)
        {
            _streamManager.DataReceived -= OnSerialDataReceived;
            _streamManager.StopStreaming();
            _streamManager.Dispose();
            _streamManager = null;
        }

        IsStreaming = false;
    }

    /// <summary>Reader-thread callback; hands the chunk to <see cref="Ingest"/>.</summary>
    private void OnSerialDataReceived(byte[] buffer, int count) => Ingest(buffer, count);

    /// <summary>
    /// Appends a chunk of received bytes to the receive buffer.
    /// </summary>
    /// <param name="buffer">Source bytes. Not retained — the contents are copied.</param>
    /// <param name="count">Number of valid bytes at the start of <paramref name="buffer"/>.</param>
    /// <remarks>
    /// Public so the frame parser can be driven without a serial port and a board attached. The
    /// parser is the whole substance of this driver and the one part a wrong byte offset would
    /// corrupt silently, so it has to be reachable from a test.
    /// </remarks>
    public void Ingest(byte[] buffer, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        count = Math.Clamp(count, 0, buffer.Length);
        if (count == 0) return;

        lock (_bufferLock)
        {
            // Overflow: the parser is clocked, so a slow or stopped clock lets the reader run
            // away. Drop the oldest bytes rather than the newest - the live signal is never the
            // data thrown away.
            int overflow = _rxCount + count - _rxBuffer.Length;
            if (overflow > 0)
            {
                int keep = Math.Max(0, _rxCount - overflow);
                if (keep > 0) Array.Copy(_rxBuffer, _rxCount - keep, _rxBuffer, 0, keep);
                _rxCount = keep;

                Info.Flag = "Overflow";
                Info.FrameErrors++;
            }

            // A single read larger than the whole buffer can only be honoured in part; take its
            // tail, which is the most recent data on the wire.
            int take   = Math.Min(count, _rxBuffer.Length - _rxCount);
            int offset = count - take;
            Array.Copy(buffer, offset, _rxBuffer, _rxCount, take);
            _rxCount += take;
        }
    }

    #endregion

    #region Data Processing

    /// <summary>
    /// Takes one frame from the receive buffer on each clock tick and publishes the sample.
    /// </summary>
    /// <param name="sender">The upstream clock.</param>
    /// <param name="value">The tick timestamp; unused.</param>
    protected override void OnReceive(object sender, object value)
    {
        if (TryParseFrame())
            Info.FramesProcessed++;

        // Published unconditionally: a tick with no complete frame yet republishes the previous
        // sample, so the downstream rate stays at the clock rate instead of stuttering.
        var output = Vector.Build.DenseOfArray(_sample);
        Viz.Feed(output);
        Publish(output);
    }

    /// <summary>
    /// Finds, validates and decodes the frame at the head of the receive buffer into
    /// <see cref="_sample"/>.
    /// </summary>
    /// <returns><see langword="true"/> when a frame was decoded.</returns>
    private bool TryParseFrame()
    {
        lock (_bufferLock)
        {
            if (_bytesPerFrame <= 0 || _rxCount < _bytesPerFrame) return false;

            int start = IndexOfFrameStart();
            if (start < 0)
            {
                // Nothing buffered can begin a frame, so drop it all - otherwise a burst of
                // noise sits at the head of the buffer forever and wedges the parser. The last
                // byte is kept: it may be the first half of a marker split across two reads.
                Info.Flag = "Frame not found";
                Info.FrameErrors++;
                Consume(_rxCount - 1);
                return false;
            }

            // Marker found but the frame has not fully arrived: realign and wait.
            if (_rxCount - start < _bytesPerFrame)
            {
                Consume(start);
                return false;
            }

            int declaredLength = _rxBuffer[start + 2];
            if (declaredLength != _bytesPerFrame)
            {
                // Consume past the marker only, never the declared length: a corrupt length
                // byte is exactly the value that must not be trusted to size a skip.
                Info.Flag = "Length mismatch";
                Info.FrameErrors++;
                Consume(start + 2);
                return false;
            }

            if (_rxBuffer[start + _bytesPerFrame - 2] != FrameEnd ||
                _rxBuffer[start + _bytesPerFrame - 1] != FrameEnd)
            {
                // Consume it anyway - a malformed frame must not be retried forever.
                Info.Flag = "Frame ending error";
                Info.FrameErrors++;
                Consume(start + _bytesPerFrame);
                return false;
            }

            for (int ch = 0, o = start + HeaderBytes; ch < _numOfChannels; ch++, o += BytesPerChannel)
            {
                int raw = (_rxBuffer[o] << 8) | _rxBuffer[o + 1];
                _sample[ch] = ReferenceVolts * raw / FullScaleCounts;
            }

            Consume(start + _bytesPerFrame);
            Info.Flag = "Streaming";
            return true;
        }
    }

    /// <summary>
    /// Index of the first <c>0xAA 0x7A</c> marker in the buffer, or -1. Caller holds
    /// <see cref="_bufferLock"/>.
    /// </summary>
    private int IndexOfFrameStart()
    {
        for (int i = 0; i < _rxCount - 1; i++)
            if (_rxBuffer[i] == FrameStart0 && _rxBuffer[i + 1] == FrameStart1)
                return i;

        return -1;
    }

    /// <summary>
    /// Discards the first <paramref name="count"/> bytes of the buffer. Caller holds
    /// <see cref="_bufferLock"/>.
    /// </summary>
    private void Consume(int count)
    {
        if (count <= 0) return;
        if (count >= _rxCount) { _rxCount = 0; return; }

        Array.Copy(_rxBuffer, count, _rxBuffer, 0, _rxCount - count);
        _rxCount -= count;
    }

    #endregion

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "DlrAdcBt";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams() =>
        [PortNumber, NumOfChannels, BaudRate];

    #endregion

    #region Dispose

    /// <summary>Stops the board, closes the port and releases the visualization.</summary>
    public override void Dispose()
    {
        if (_disposed)
        {
            base.Dispose();
            return;
        }

        _disposed = true;
        Disconnect();

        try { Viz.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"[{Name}] Viz dispose failed: {ex.Message}"); }

        base.Dispose();
    }

    #endregion
}

/// <summary>
/// Observable status model for the <see cref="DlrAdcBt"/> block.
/// </summary>
public partial class DlrAdcBtInfo : ObservableObject
{
    /// <summary>Current status flag (e.g. "Streaming", "Overflow", "Disconnected").</summary>
    [ObservableProperty] private string _flag = "Ready";

    /// <summary>Running count of frames successfully decoded.</summary>
    [ObservableProperty] private int _framesProcessed;

    /// <summary>
    /// Running count of parser faults (overflow, missing start marker, bad length, bad trailer).
    /// </summary>
    /// <remarks>
    /// <see cref="Flag"/> is overwritten on every decoded frame, so an error state survives
    /// roughly one tick and a UI poll almost never observes it. This counter is monotonic, so a
    /// poll can compare it against its previous reading.
    /// </remarks>
    [ObservableProperty] private int _frameErrors;
}
