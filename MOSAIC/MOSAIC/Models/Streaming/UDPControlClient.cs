using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;

namespace MOSAIC.Models.Streaming;

/// <summary>
/// UDP block for impedance-controlled actuators: streams a control signal together with the
/// proportional and derivative gains the receiver should apply to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> A specialisation of <see cref="UDPClient"/> for closed-loop actuator control.
/// It behaves identically on the receive path (packets are parsed into vectors and published
/// downstream), but every <i>outgoing</i> packet appends two extra fields — <see cref="Kp"/> then
/// <see cref="Kd"/> — after the payload channels. Use <see cref="UDPClient"/> for general-purpose
/// UDP transport; this block exists so that control-specific framing does not leak into it.
/// </para>
/// <para>
/// <b>Wire format:</b> a payload of <i>N</i> channels is sent as
/// <c>| ch0 | … | chN-1 | kp | kd |</c>, i.e. <i>N + 2</i> fields. For the common single-channel
/// case in <see cref="ParsingFormat.Float"/> that is 12 bytes, matching a Python receiver doing
/// <c>struct.unpack('&lt;fff', data)</c>. There is no way to suppress the gains — a receiver that
/// expects a bare payload will fail to unpack and drop the packet, so pair this block only with an
/// endpoint that expects the gain fields.
/// </para>
/// <para>
/// <b>Byte order:</b> all fields are written little-endian (<c>'&lt;'</c>), matching the convention
/// of the control port — not the big-endian framing used by the update/config ports.
/// </para>
/// <para>
/// <b>Runtime tuning:</b> <see cref="Kp"/> and <see cref="Kd"/> are observable and writable from the
/// UI. Both are snapshotted once per packet, so a concurrent edit can never split a gain pair
/// across two packets.
/// </para>
/// <para>
/// <b>Send-only asymmetry:</b> a remote endpoint is mandatory. Unlike <see cref="UDPClient"/> there
/// is no receive-only mode — a controller that cannot transmit has nothing to configure.
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs every published vector via <see cref="BaseBlock.Publish"/>. Note this records
/// the payload only; the gains are not part of the published vector.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "ForwardToBlender": {
///     "Type": "UDPControlClient",
///     "Params": [ 3338, "float", "172.27.7.236", 3338, 6.0, 1.0 ],
///     "Inputs": [ "Predictor" ],
///     "DesiredRate": 50
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description><c>ReceivePort</c> (int) — local port to listen on (1–65535).</description></item>
///   <item><term>1</term><description><c>ParsingFormat</c> (string) — <c>"float"</c> or <c>"double"</c>.</description></item>
///   <item><term>2</term><description><c>RemoteHost</c> (string) — target IP for sending. Required.</description></item>
///   <item><term>3</term><description><c>RemotePort</c> (int) — target port for sending. Required.</description></item>
///   <item><term>4</term><description><c>Kp</c> (double, optional) — impedance proportional gain. Defaults to 0.</description></item>
///   <item><term>5</term><description><c>Kd</c> (double, optional) — impedance derivative gain. Defaults to 0.</description></item>
/// </list>
/// </para>
/// </example>
public class UDPControlClient : BaseBlock
{
    /// <summary>Number of gain fields appended to every outgoing packet.</summary>
    private const int GainFieldCount = 2;

    /// <summary>Visualization helper for binding received/sent data to the UI scope.</summary>¬
    public BlockVisualization Viz { get; set; } = new();

    /// <summary>UDP client for sending data to the remote endpoint.</summary>
    private readonly UdpClient _sender;

    /// <summary>UDP client for receiving data on the local port.</summary>
    private readonly UdpClient _receiver;

    /// <summary>Remote endpoint the control packets are sent to. Never <see langword="null"/>.</summary>
    private readonly IPEndPoint _remoteEndpoint;

    /// <summary>Binary parsing format for UDP packets.</summary>
    private readonly ParsingFormat _format;

    /// <summary>Semaphore guarding concurrent send operations.</summary>f
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Preallocated send buffer, reallocated whenever the channel count changes.</summary>
    private byte[]? _sendBuffer;

    /// <summary>Channel count of the last payload, used to warn on an unexpected width.</summary>
    private int _lastChannelCount = -1;

    /// <summary>
    /// Number of payload channels in the most recent outgoing packet, or <c>-1</c> before the first
    /// send. The packet carries this many fields plus the two gains.
    /// </summary>
    public int PayloadChannels => _lastChannelCount;

    /// <summary>Whether the block has been disposed.</summary>
    private bool _disposed;

    /// <summary>Backing field for <see cref="Kp"/>.</summary>
    private double _kp;

    /// <summary>Backing field for <see cref="Kd"/>.</summary>
    private double _kd;

    /// <summary>
    /// Impedance proportional gain, sent as the second-to-last field of every packet.
    /// </summary>
    /// <remarks>
    /// Read once per packet on the send path; safe to write from the UI thread. Takes effect on the
    /// next packet — there is no handshake, so the receiver simply sees the new value arrive.
    /// </remarks>
    public double Kp
    {
        get => _kp;
        set => SetProperty(ref _kp, value);
    }

    /// <summary>
    /// Impedance derivative gain, sent as the last field of every packet.
    /// </summary>
    /// <remarks>
    /// Read once per packet on the send path; safe to write from the UI thread. Takes effect on the
    /// next packet.
    /// </remarks>
    public double Kd
    {
        get => _kd;
        set => SetProperty(ref _kd, value);
    }

    /// <summary>Local port the receiver is listening on.</summary>
    public int ReceivePort { get; }

    /// <summary>Remote host IP the control packets are sent to.</summary>
    public string RemoteHost { get; }

    /// <summary>Remote port the control packets are sent to.</summary>
    public int RemotePort { get; }

    /// <summary>Current parsing format.</summary>
    public ParsingFormat Format => _format;

    /// <summary>Size in bytes of a single field on the wire.</summary>
    public int FieldSize => _format == ParsingFormat.Float ? sizeof(float) : sizeof(double);

    /// <summary>Total UDP packets sent.</summary>
    public long PacketsSent { get; private set; }

    /// <summary>Total UDP packets received.</summary>
    public long PacketsReceived { get; private set; }

    /// <summary>Total bytes sent.</summary>
    public long BytesSent { get; private set; }

    /// <summary>Total bytes received.</summary>
    public long BytesReceived { get; private set; }

    /// <summary>Raised after a packet is sent.</summary>
    public event EventHandler<PacketEventArgs>? PacketSentEvent;

    /// <summary>Raised after a packet is received.</summary>
    public event EventHandler<PacketEventArgs>? PacketReceivedEvent;

    /// <summary>Raised when the block's status changes (e.g. errors).</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Supported binary data formats for UDP packets.</summary>
    public enum ParsingFormat { Float, Double }

    /// <summary>Internal state for async receive callbacks.</summary>
    private readonly struct UdpState
    {
        public UdpClient Client { get; init; }
        public IPEndPoint EndPoint { get; init; }
    }

    /// <summary>
    /// Initializes a new <see cref="UDPControlClient"/> block.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="desiredRate">Processing rate in Hz (typically inherited).</param>
    /// <param name="receivePort">Local port to listen on (1–65535).</param>
    /// <param name="format">Binary parsing format.</param>
    /// <param name="remoteHost">Remote host IP for sending. Required.</param>
    /// <param name="remotePort">Remote port for sending. Required.</param>
    /// <param name="kp">Initial impedance proportional gain.</param>
    /// <param name="kd">Initial impedance derivative gain.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if either port is out of valid range.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="remoteHost"/> is missing or unparsable.</exception>
    public UDPControlClient(
        string name,
        double desiredRate,
        int receivePort,
        ParsingFormat format,
        string remoteHost,
        int remotePort,
        double kp,
        double kd) : base(name, desiredRate)
    {
        if (receivePort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(receivePort), $"Port must be between 1 and 65535, got {receivePort}");

        if (remotePort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(remotePort), $"Port must be between 1 and 65535, got {remotePort}");

        if (string.IsNullOrWhiteSpace(remoteHost))
            throw new ArgumentException("A remote host is required — this block has no receive-only mode.", nameof(remoteHost));

        if (!IPAddress.TryParse(remoteHost, out var remoteAddress))
            throw new ArgumentException($"'{remoteHost}' is not a valid IP address.", nameof(remoteHost));

        _format = format;
        _kp = kp;
        _kd = kd;
        ReceivePort = receivePort;
        RemoteHost = remoteHost;
        RemotePort = remotePort;

        _remoteEndpoint = new IPEndPoint(remoteAddress, remotePort);
        _sender = new UdpClient();
        _sender.Connect(_remoteEndpoint);
        Debug.WriteLine($"[{Name}] Control output to {remoteHost}:{remotePort} with kp={kp}, kd={kd}");

        var localEndpoint = new IPEndPoint(IPAddress.Any, receivePort);
        _receiver = new UdpClient(localEndpoint);
        Debug.WriteLine($"[{Name}] Listening on port {receivePort}");

        StartReceiving();
        StatusChanged?.Invoke(this, "Running");
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_udpctrl.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_udpctrl";

    /// <summary>
    /// Creates a <see cref="UDPControlClient"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// JSON model. See the class-level example for <c>Params</c> layout.
    /// </param>
    /// <returns>A configured <see cref="UDPControlClient"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown if required parameters are missing or invalid.</exception>
    public static UDPControlClient ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var p = m.Params ?? new List<object>(0);
        var name = m.Name ?? "UDPControlClient";
        var rate = m.DesiredRate ?? 0;

        if (p.Count < 4)
            throw new ArgumentException(
                $"UDP Control Client '{name}' requires at least 4 parameters: " +
                "[receivePort, parsingFormat, remoteHost, remotePort, kp?, kd?]");

        var receivePort = JsonModel.GetInt(p[0], 0);
        if (receivePort is <= 0 or > 65535)
            throw new ArgumentException($"UDP Control Client '{name}': receivePort must be between 1 and 65535, got {receivePort}");

        var format = ParseFormat(JsonModel.GetString(p[1], "float"));

        var remoteHost = JsonModel.GetString(p[2], null);
        if (string.IsNullOrWhiteSpace(remoteHost))
            throw new ArgumentException($"UDP Control Client '{name}': remoteHost is required.");

        var remotePort = JsonModel.GetInt(p[3], 0);
        if (remotePort is <= 0 or > 65535)
            throw new ArgumentException($"UDP Control Client '{name}': remotePort must be between 1 and 65535, got {remotePort}");

        var kp = p.Count > 4 ? JsonModel.GetDouble(p[4], 0.0) : 0.0;
        var kd = p.Count > 5 ? JsonModel.GetDouble(p[5], 0.0) : 0.0;

        Debug.WriteLine($"[{name}] ConfigureInput: receivePort={receivePort}, format={format}, " +
                        $"remoteHost={remoteHost}, remotePort={remotePort}, kp={kp}, kd={kd}");

        var block = ActivatorUtilities.CreateInstance<UDPControlClient>(
            sp, name, rate, receivePort, format, remoteHost, remotePort, kp, kd);

        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "UDPControlClient";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams() => new List<object>
    {
        ReceivePort,
        Format.ToString().ToLowerInvariant(),
        RemoteHost,
        RemotePort,
        Kp,
        Kd
    };

    #endregion

    /// <summary>
    /// Sends upstream data with the current gains appended, and publishes the payload downstream.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="data">
    /// Expected to be a <see cref="Vector{T}"/> of <see cref="double"/>.
    /// Other types are logged and ignored.
    /// </param>
    /// <remarks>
    /// Only the payload is published and visualised — the gains are transmission metadata, not part
    /// of the dataflow.
    /// </remarks>
    protected override void OnReceive(object sender, object data)
    {
        if (data is null || _disposed) return;

        if (data is not Vector<double> vector)
        {
            Debug.WriteLine($"[{Name}] Expected Vector<double>, got {data.GetType().Name}");
            return;
        }

        if (vector.Count == 0) return;

        if (vector.Count != _lastChannelCount)
        {
            // The packet length is (N + 2) fields; a receiver unpacking a fixed struct will start
            // dropping everything if N changes mid-run, so make the change visible.
            if (_lastChannelCount >= 0)
                Debug.WriteLine($"[{Name}] Payload width changed {_lastChannelCount} → {vector.Count}; " +
                                $"packet is now {(vector.Count + GainFieldCount) * FieldSize} bytes.");
            _lastChannelCount = vector.Count;
        }

        SendUdpAsync(vector);
        Publish(vector);
        Viz?.Feed(vector);
    }

    /// <summary>Serialises and sends a control packet asynchronously over UDP.</summary>
    /// <remarks>
    /// Payload channels are written little-endian in order, followed by <see cref="Kp"/> and
    /// <see cref="Kd"/>. Both gains are snapshotted once per packet so a concurrent UI edit cannot
    /// split across two packets.
    /// </remarks>
    private async void SendUdpAsync(Vector<double> vector)
    {
        try
        {
            await _sendLock.WaitAsync();

            var kp = Kp;
            var kd = Kd;

            var requiredBytes = (vector.Count + GainFieldCount) * FieldSize;

            var buffer = _sendBuffer;
            if (buffer == null || buffer.Length != requiredBytes)
            {
                buffer = new byte[requiredBytes];
                _sendBuffer = buffer;
            }

            FillSendBuffer(buffer, vector, kp, kd);

            await _sender.SendAsync(buffer, buffer.Length);

            PacketsSent++;
            BytesSent += buffer.Length;
            PacketSentEvent?.Invoke(this, new PacketEventArgs(buffer.Length));
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Send Error: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Writes the payload channels followed by the two gains into <paramref name="buffer"/> as
    /// little-endian fields of the configured format.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="SendUdpAsync"/> because a <see cref="Span{T}"/> local cannot be
    /// declared in an <c>async</c> method. Callers must hold the send lock and must size
    /// <paramref name="buffer"/> exactly.
    /// </remarks>
    private void FillSendBuffer(byte[] buffer, Vector<double> vector, double kp, double kd)
    {
        var span = buffer.AsSpan();
        var offset = 0;

        if (_format == ParsingFormat.Float)
        {
            for (int i = 0; i < vector.Count; i++, offset += sizeof(float))
                BinaryPrimitives.WriteSingleLittleEndian(span[offset..], (float)vector[i]);

            BinaryPrimitives.WriteSingleLittleEndian(span[offset..], (float)kp);
            offset += sizeof(float);
            BinaryPrimitives.WriteSingleLittleEndian(span[offset..], (float)kd);
        }
        else
        {
            for (int i = 0; i < vector.Count; i++, offset += sizeof(double))
                BinaryPrimitives.WriteDoubleLittleEndian(span[offset..], vector[i]);

            BinaryPrimitives.WriteDoubleLittleEndian(span[offset..], kp);
            offset += sizeof(double);
            BinaryPrimitives.WriteDoubleLittleEndian(span[offset..], kd);
        }
    }

    /// <summary>Starts the asynchronous receive loop.</summary>
    private void StartReceiving()
    {
        if (_disposed) return;

        var state = new UdpState
        {
            Client = _receiver,
            EndPoint = new IPEndPoint(IPAddress.Any, 0)
        };

        try { _receiver.BeginReceive(AsyncReceiveCallback, state); }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"Receive Error: {ex.Message}"); }
    }

    /// <summary>Handles an incoming UDP packet, parses it, and publishes the result.</summary>
    private void AsyncReceiveCallback(IAsyncResult ar)
    {
        if (_disposed) return;

        var state = (UdpState)ar.AsyncState!;
        var client = state.Client;
        var endpoint = state.EndPoint;

        try
        {
            byte[] receivedBytes = client.EndReceive(ar, ref endpoint);

            if (receivedBytes.Length > 0)
            {
                var receivedValue = ParseReceivedData(receivedBytes);

                if (receivedValue is not null && receivedValue.Count > 0)
                {
                    Publish(receivedValue);
                    Viz?.Feed(receivedValue);

                    PacketsReceived++;
                    BytesReceived += receivedBytes.Length;
                    PacketReceivedEvent?.Invoke(this, new PacketEventArgs(receivedBytes.Length));
                }
            }

            StartReceiving();
        }
        catch (ObjectDisposedException) { }
        catch (SocketException sex)
        {
            StatusChanged?.Invoke(this, $"Socket Error: {sex.SocketErrorCode}");
            if (!_disposed) StartReceiving();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Receive error: {ex.Message}");
            StatusChanged?.Invoke(this, $"Error: {ex.Message}");
            if (!_disposed) StartReceiving();
        }
    }

    /// <summary>Parses received bytes into a vector. Returns <see langword="null"/> on failure.</summary>
    private Vector<double>? ParseReceivedData(byte[] bytes)
    {
        try
        {
            return _format switch
            {
                ParsingFormat.Float => ParseAsFloats(bytes),
                ParsingFormat.Double => ParseAsDoubles(bytes),
                _ => null
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Parse error ({bytes.Length} bytes): {ex.Message}");
            return null;
        }
    }

    /// <summary>Parses a byte array as 32-bit floats and returns a double vector.</summary>
    private static Vector<double> ParseAsFloats(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
            throw new ArgumentException($"Byte array length ({bytes.Length}) is not a multiple of float size (4).");

        var floatCount = bytes.Length / sizeof(float);
        var floats = new float[floatCount];
        System.Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);

        return Vector<double>.Build.DenseOfArray(Array.ConvertAll(floats, f => (double)f));
    }

    /// <summary>Parses a byte array as 64-bit doubles and returns a vector.</summary>
    private static Vector<double> ParseAsDoubles(byte[] bytes)
    {
        if (bytes.Length % sizeof(double) != 0)
            throw new ArgumentException($"Byte array length ({bytes.Length}) is not a multiple of double size (8).");

        var doubleCount = bytes.Length / sizeof(double);
        var doubles = new double[doubleCount];
        System.Buffer.BlockCopy(bytes, 0, doubles, 0, bytes.Length);

        return Vector<double>.Build.DenseOfArray(doubles);
    }

    /// <summary>Parses a format string into a <see cref="ParsingFormat"/> enum value.</summary>
    private static ParsingFormat ParseFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return ParsingFormat.Float;

        return format.ToLowerInvariant() switch
        {
            "float" => ParsingFormat.Float,
            "double" => ParsingFormat.Double,
            _ => throw new ArgumentException($"Unsupported parsing format '{format}'. Supported: Float, Double.")
        };
    }

    /// <summary>
    /// Disposes UDP sockets, semaphore, visualization, and base class resources
    /// (including CSV dumper if configured).
    /// </summary>
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        base.Dispose();
        Viz?.Dispose();

        _sendLock?.Dispose();
        _receiver?.Dispose();
        _sender?.Dispose();
    }
}
