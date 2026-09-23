using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;
using MOSAIC.Visualization;

namespace MOSAIC.Models.Streaming;

/// <summary>
/// Bidirectional UDP communication block for streaming vector data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> The UDPClient block provides both sending and receiving of
/// <see cref="Vector{T}"/> data over UDP. Received packets are parsed into vectors
/// and published downstream. Upstream data arriving via <see cref="BaseBlock.OnReceive"/>
/// is serialised and sent to the configured remote endpoint.
/// </para>
/// <para>
/// <b>Parsing formats:</b>
/// <list type="bullet">
///   <item><description><see cref="ParsingFormat.Float"/> — 32-bit IEEE 754 floats (4 bytes per channel).</description></item>
///   <item><description><see cref="ParsingFormat.Double"/> — 64-bit IEEE 754 doubles (8 bytes per channel).</description></item>
///   <item><description><see cref="ParsingFormat.Uint16"/> — unsigned 16-bit little-endian integers (2 bytes per value).</description></item>
/// </list>
/// The number of channels is inferred from the packet size.
/// </para>
/// <para>
/// <b>Receive-only mode:</b> If <c>remoteHost</c> and <c>remotePort</c> are omitted,
/// the block only listens on the configured receive port.
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs every published vector via <see cref="BaseBlock.Publish"/>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "UDPStream": {
///     "Type": "UDPClient",
///     "Params": [ 5005, "double", "127.0.0.1", 5006 ],
///     "DesiredRate": 200,
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description><c>ReceivePort</c> (int) — local port to listen on (1–65535).</description></item>
///   <item><term>1</term><description><c>ParsingFormat</c> (string) — <c>"float"</c>, <c>"double"</c> or <c>"uint16"</c>.</description></item>
///   <item><term>2</term><description><c>RemoteHost</c> (string, optional) — target IP for sending.</description></item>
///   <item><term>3</term><description><c>RemotePort</c> (int, optional) — target port for sending. Must be specified together with RemoteHost.</description></item>
/// </list>
/// </para>
/// </example>
public class UDPClient : BaseBlock, IReceivePort
{
    /// <summary>Visualization helper for binding received/sent data to the UI scope.</summary>
    public BlockVisualization Viz { get; set; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>UDP client for sending data to the remote endpoint.</summary>
    private readonly UdpClient _sender;

    /// <summary>UDP client for receiving data on the local port.</summary>
    private readonly UdpClient _receiver;

    /// <summary>Remote endpoint for sending, or <see langword="null"/> for receive-only mode.</summary>
    private readonly IPEndPoint? _remoteEndpoint;

    /// <summary>Binary parsing format for UDP packets.</summary>
    private readonly ParsingFormat _format;

    /// <summary>Semaphore guarding concurrent send operations.</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Number of channels in the current send buffer.</summary>
    private int _channelNum;

    /// <summary>Preallocated send buffer, resized when channel count changes.</summary>
    private byte[]? _sendBuffer;

    /// <summary>Whether the block has been disposed.</summary>
    private bool _disposed;

    /// <summary>Local port the receiver is listening on.</summary>
    public int ReceivePort { get; set; }

    /// <summary>Remote host IP for sending, or <see langword="null"/> for receive-only.</summary>
    public string? RemoteHost { get; set; }

    /// <summary>Remote port for sending, or <see langword="null"/> for receive-only.</summary>
    public int? RemotePort { get; set; }

    /// <summary>Current parsing format.</summary>
    public ParsingFormat Format => _format;

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
    public enum ParsingFormat { Float, Double, Uint16 }

    /// <summary>Internal state for async receive callbacks.</summary>
    private readonly struct UdpState
    {
        public UdpClient Client { get; init; }
        public IPEndPoint EndPoint { get; init; }
    }

    /// <summary>
    /// Zero inputs — the block is a pure receiver, publishing whatever arrives on
    /// <see cref="ReceivePort"/>. That is the common configuration, and declaring it here
    /// is what makes the block a valid graph source in the palette and the graph editor.
    /// </summary>
    public override int MinInputs => 0;

    /// <summary>
    /// At most one input. Attaching an upstream block additionally serialises its vectors
    /// to the configured remote endpoint; leaving it unattached is receive-only mode.
    /// </summary>
    public override int MaxInputs => 1;

    /// <summary>
    /// Initializes a new <see cref="UDPClient"/> block.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="desiredRate">Processing rate in Hz (typically inherited).</param>
    /// <param name="receivePort">Local port to listen on (1–65535).</param>
    /// <param name="format">Binary parsing format.</param>
    /// <param name="remoteHost">Remote host IP for sending, or <see langword="null"/> for receive-only.</param>
    /// <param name="remotePort">Remote port for sending, or <see langword="null"/> for receive-only.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if ports are out of valid range.</exception>
    public UDPClient(
        string name,
        double desiredRate,
        int receivePort,
        ParsingFormat format,
        string? remoteHost = null,
        int? remotePort = null) : base(name, desiredRate)
    {
        if (receivePort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(receivePort), $"Port must be between 1 and 65535, got {receivePort}");

        _format = format;
        ReceivePort = receivePort;
        RemoteHost = remoteHost;
        RemotePort = remotePort;

        _sender = new UdpClient();

        if (!string.IsNullOrWhiteSpace(remoteHost) && remotePort is > 0 and <= 65535)
        {
            _remoteEndpoint = new IPEndPoint(IPAddress.Parse(remoteHost), remotePort.Value);
            _sender.Connect(_remoteEndpoint);
            Debug.WriteLine($"[{Name}] Configured to send to {remoteHost}:{remotePort}");
        }

        var localEndpoint = new IPEndPoint(IPAddress.Any, receivePort);
        _receiver = new UdpClient(localEndpoint);
        Debug.WriteLine($"[{Name}] Listening on port {receivePort}");

        StartReceiving();
        StatusChanged?.Invoke(this, "Running");
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_udp.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_udp";

    /// <summary>
    /// Creates a <see cref="UDPClient"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// JSON model. See the class-level example for <c>Params</c> layout.
    /// </param>
    /// <returns>A configured <see cref="UDPClient"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown if required parameters are missing or invalid.</exception>
    public static UDPClient ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var p = m.Params;
        var name = m.Name ?? "UDPClient";
        var rate = m.DesiredRate ?? 0;

        // if (p.Count < 2)
        //     throw new ArgumentException(
        //         $"UDP Client '{name}' requires at least 2 parameters: [receivePort, parsingFormat, remoteHost?, remotePort?]");

        var receivePort = JsonModel.GetInt(p is { Count: > 0 } ? p[0] : null, 3342);
        if (receivePort is <= 0 or > 65535)
            throw new ArgumentException($"UDP Client '{name}': receivePort must be between 1 and 65535, got {receivePort}");

        var formatStr = JsonModel.GetString(p is { Count: > 1 } ? p[1] : null, "double");
        var format = ParseFormat(formatStr);

        string? remoteHost = p?.Count > 2 ? JsonModel.GetString(p[2], null) : null;
        int? remotePort = p?.Count > 3 ? JsonModel.GetInt(p[3], 0) : null;

        if (string.IsNullOrWhiteSpace(remoteHost)) remoteHost = null;
        if (remotePort is null or <= 0 or > 65535) remotePort = null;

        if ((remoteHost != null) != (remotePort != null))
            throw new ArgumentException(
                $"UDP Client '{name}': Both remoteHost and remotePort must be specified together or both omitted.");

        Debug.WriteLine($"[{name}] ConfigureInput: receivePort={receivePort}, format={format}, " +
                        $"remoteHost={remoteHost ?? "null"}, remotePort={remotePort?.ToString() ?? "null"}");

        var block = (remoteHost != null && remotePort != null)
            ? ActivatorUtilities.CreateInstance<UDPClient>(sp, name, rate, receivePort, format, remoteHost, remotePort.Value)
            : ActivatorUtilities.CreateInstance<UDPClient>(sp, name, rate, receivePort, format);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "UDPClient";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
    {
        var list = new List<object> { ReceivePort, Format.ToString().ToLowerInvariant() };
        if (RemoteHost != null && RemotePort != null)
        {
            list.Add(RemoteHost);
            list.Add(RemotePort.Value);
        }
        return list;
    }

    #endregion

    /// <summary>
    /// Sends upstream data over UDP and publishes it downstream.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="data">
    /// Expected to be a <see cref="Vector{T}"/> of <see cref="double"/>.
    /// Other types are logged and ignored.
    /// </param>
    protected override void OnReceive(object sender, object data)
    {
        if (data is null || _disposed) return;

        if (data is not Vector<double> vector)
        {
            Debug.WriteLine($"[{Name}] Expected Vector<double>, got {data.GetType().Name}");
            return;
        }

        if (vector.Count == 0) return;

        SendUdpAsync(vector);
        Publish(vector);
        Viz?.Feed(vector);
    }

    /// <summary>Serialises and sends a vector asynchronously over UDP.</summary>
    private async void SendUdpAsync(Vector<double> vector)
    {
        bool acquired = false;
        try
        {
            await _sendLock.WaitAsync();
            acquired = true;

            if (_disposed || _remoteEndpoint == null) return;

            if (_sendBuffer == null || _channelNum != vector.Count)
            {
                _channelNum = vector.Count;
                int fieldSize = _format switch
                {
                    ParsingFormat.Float => sizeof(float),
                    ParsingFormat.Uint16 => sizeof(ushort),
                    _ => sizeof(double)
                };
                _sendBuffer = new byte[fieldSize * _channelNum];
            }

            if (_format == ParsingFormat.Float)
            {
                var floats = new float[_channelNum];
                for (int i = 0; i < _channelNum; i++)
                    floats[i] = (float)vector[i];
                System.Buffer.BlockCopy(floats, 0, _sendBuffer, 0, _sendBuffer.Length);
            }
            else if (_format == ParsingFormat.Uint16)
            {
                for (int i = 0; i < _channelNum; i++)
                {
                    double value = vector[i];
                    if (!double.IsFinite(value) || value < 0 || value > ushort.MaxValue || value != Math.Truncate(value))
                        throw new ArgumentException("uint16 UDP values must be integers between 0 and 65535.");
                    BinaryPrimitives.WriteUInt16LittleEndian(_sendBuffer.AsSpan(i * 2, 2), (ushort)value);
                }
            }
            else
            {
                System.Buffer.BlockCopy(vector.ToArray(), 0, _sendBuffer, 0, _sendBuffer.Length);
            }

            await _sender.SendAsync(_sendBuffer, _sendBuffer.Length);

            PacketsSent++;
            BytesSent += _sendBuffer.Length;
            PacketSentEvent?.Invoke(this, new PacketEventArgs(_sendBuffer.Length));
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Send Error: {ex.Message}");
        }
        finally
        {
            if (acquired)
            {
                try { _sendLock.Release(); }
                catch (ObjectDisposedException) { }
            }
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
                ParsingFormat.Uint16 => ParseAsUInt16(bytes),
                _ => null
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Parse error ({bytes.Length} bytes): {ex.Message}");
            return null;
        }
    }

    /// <summary>Parses unsigned 16-bit little-endian values, including values above 32767.</summary>
    private static Vector<double> ParseAsUInt16(byte[] bytes)
    {
        if (bytes.Length % sizeof(ushort) != 0)
            throw new ArgumentException("uint16 UDP packets must contain a whole number of two-byte values.");
        var values = new double[bytes.Length / sizeof(ushort)];
        for (int i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
        return Vector<double>.Build.DenseOfArray(values);
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
            return ParsingFormat.Double;

        return format.Trim().ToLowerInvariant() switch
        {
            "float" => ParsingFormat.Float,
            "double" => ParsingFormat.Double,
            "uint16" => ParsingFormat.Uint16,
            _ => throw new ArgumentException($"Unsupported parsing format '{format}'. Supported: Float, Double, Uint16.")
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

/// <summary>Event arguments for UDP packet send/receive events.</summary>
public class PacketEventArgs : EventArgs
{
    /// <summary>Number of bytes in the packet.</summary>
    public int ByteCount { get; }

    /// <summary>Timestamp when the event occurred.</summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// Initializes a new <see cref="PacketEventArgs"/>.
    /// </summary>
    /// <param name="byteCount">Number of bytes in the packet.</param>
    public PacketEventArgs(int byteCount)
    {
        ByteCount = byteCount;
        Timestamp = DateTime.Now;
    }
}
