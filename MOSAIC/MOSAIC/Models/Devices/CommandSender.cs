using System.Collections.Generic;
using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;

namespace MOSAIC.Models.Devices;

/// <summary>
/// Supported IMU data stream types that can be requested from an ESP sensor node.
/// </summary>
public enum EspStreamType
{
    /// <summary>Rotation quaternion stream.</summary>
    ROT,

    /// <summary>Accelerometer stream.</summary>
    ACC,

    /// <summary>Gyroscope stream.</summary>
    GYR,

    /// <summary>Magnetometer stream.</summary>
    MAG,

    /// <summary>All sensor streams simultaneously.</summary>
    ALL
}

/// <summary>
/// A <see cref="BaseBlock"/> that sends UDP command strings to ESP-based IMU sensor nodes
/// to control which data streams are active.
/// </summary>
/// <remarks>
/// <para>
/// The command protocol is a simple ASCII string sent over UDP:
/// <c>{StreamType}{DeviceNumber}{on|off}</c>, for example <c>ROT1on</c> or <c>ACC3off</c>.
/// </para>
/// <para>
/// This block does not receive any pipeline data — it is a pure command sender.
/// The host IP and port can be configured via JSON or changed at runtime through
/// the <see cref="MOSAIC.ViewModels.Devices.CommandSenderViewModel"/>.
/// </para>
/// </remarks>
/// <example>
/// <para>JSON pipeline configuration:</para>
/// <code language="json">
/// {
///   "Name": "ESP-Control",
///   "Type": "CommandSender",
///   "Params": [ "192.168.1.100", "5000" ]
/// }
/// </code>
/// </example>
public sealed partial class CommandSender : BaseBlock, IDisposable
{
    private UdpClient? _udpClient;
    private bool _isConnected;

    /// <summary>Target ESP host IP address.</summary>
    public string Host { get; set; }

    /// <summary>Target UDP port number.</summary>
    public int Port { get; set; }

    /// <summary>The two values <c>ConfigureInput</c> reads back, in its order: host, then port.</summary>
    protected override IReadOnlyList<object>? GetJsonParams() => [Host, Port];
    
    public override int MinInputs => 0;
    public override int MaxInputs => 0;

    /// <summary>Observable status information for UI binding.</summary>
    public CommandSenderInfo Info { get; } = new();

    /// <summary>
    /// Initialises a new <see cref="CommandSender"/> instance.
    /// </summary>
    /// <param name="name">Display name for this block.</param>
    /// <param name="host">Target ESP host IP address.</param>
    /// <param name="port">Target UDP port number.</param>
    public CommandSender(string name = "CommandSender", string host = "192.168.1.1", int port = 5000)
    {
        Name = name;
        DesiredRate = 0;
        Host = host;
        Port = port;
    }

    /// <summary>
    /// Factory method that creates a <see cref="CommandSender"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">The application's dependency injection service provider.</param>
    /// <param name="m">
    /// JSON model. <c>Params</c> must contain exactly two entries: <c>[hostIP, port]</c>.
    /// </param>
    /// <returns>A configured <see cref="CommandSender"/> block.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown if <c>Params</c> does not contain exactly two entries.
    /// </exception>
    public static CommandSender ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "CommandSender";

        // Require an explicit endpoint because UDP cannot confirm that a datagram reached the
        // intended subnet; a guessed default could silently target the wrong network.
        if (m.Params is not { Count: 2 })
            throw new ArgumentException(
                $"CommandSender '{name}' requires exactly 2 Params: [hostIP, port].");

        var host = m.Params[0]?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException($"CommandSender '{name}': Params[0] (hostIP) must not be empty.");

        var portStr = m.Params[1]?.ToString()?.Trim();
        if (!int.TryParse(portStr, out int port))
            throw new ArgumentException(
                $"CommandSender '{name}': Params[1] (port) must be an integer, got '{portStr}'.");

        if (port is <= 0 or > 65535)
            throw new ArgumentException(
                $"CommandSender '{name}': port must be between 1 and 65535, got {port}.");

        var block = ActivatorUtilities.CreateInstance<CommandSender>(sp, name, host, port);

        // Connect straight away, the way the legacy block did in ConfigureInputs(). UDP
        // "connect" only records the default remote endpoint — it sends nothing and cannot
        // block — so it is safe at load time, and it means a freshly loaded pipeline can
        // send a command without the user first having to press Connect on the card.
        // Failures are recorded rather than thrown: a bad address in one block must not
        // take down the whole pipeline load.
        block.TryConnect();

        return block;
    }

    /// <summary>
    /// Whether the UDP client currently has an endpoint bound.
    /// </summary>
    public bool IsConnected => _isConnected;

    /// <summary>
    /// Opens the UDP connection to the target ESP node.
    /// If already connected, the old client is closed first.
    /// </summary>
    /// <exception cref="SocketException">The host could not be resolved.</exception>
    public void Connect()
    {
        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = new UdpClient();

        // Broadcast addresses (x.x.x.255) are the normal case for reaching the ESP mesh.
        _udpClient.EnableBroadcast = true;
        _udpClient.Connect(Host, Port);

        _isConnected = true;
        Info.Status = "Connected";
        Info.Target = $"{Host}:{Port}";
        Console.WriteLine($"[CommandSender] Connected to {Host}:{Port}");
    }

    /// <summary>
    /// Attempts to connect, reporting failure through <see cref="Info"/> instead of throwing.
    /// </summary>
    /// <returns><see langword="true"/> if the endpoint was bound.</returns>
    public bool TryConnect()
    {
        try
        {
            Connect();
            return true;
        }
        catch (Exception ex)
        {
            _isConnected = false;
            Info.Status = $"Connection failed: {ex.Message}";
            Info.Target = $"{Host}:{Port}";
            Console.WriteLine($"[CommandSender] {Info.Status}");
            return false;
        }
    }

    /// <summary>
    /// Closes the UDP client without disposing the block, so it can be reconnected
    /// to a different host or port.
    /// </summary>
    public void Disconnect()
    {
        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;
        _isConnected = false;
        Info.Status = "Disconnected";
        Console.WriteLine("[CommandSender] Disconnected.");
    }

    /// <summary>
    /// Sends a command to start or stop a specific data stream on a specific device.
    /// </summary>
    /// <param name="streamType">The sensor stream to control.</param>
    /// <param name="deviceNumber">The 1-based device index on the ESP network.</param>
    /// <param name="enable">
    /// <see langword="true"/> to start the stream, <see langword="false"/> to stop it.
    /// </param>
    /// <returns>A task that completes when the UDP datagram has been sent.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="Connect"/> has not been called.
    /// </exception>
    public async Task SendCommand(EspStreamType streamType, int deviceNumber, bool enable)
    {
        if (!_isConnected || _udpClient is null)
            throw new InvalidOperationException("CommandSender is not connected. Call Connect() first.");

        string command = $"{streamType}{deviceNumber}{(enable ? "on" : "off")}";
        byte[] data = Encoding.ASCII.GetBytes(command);
        await _udpClient.SendAsync(data, data.Length);

        Info.LastCommand = command;
        Info.CommandsSent++;
        Console.WriteLine($"[CommandSender] Sent: {command}");
    }

    /// <summary>
    /// Sends a raw string command (for custom or future protocol extensions).
    /// </summary>
    /// <param name="rawCommand">The ASCII command string to send.</param>
    /// <returns>A task that completes when the datagram has been sent.</returns>
    public async Task SendRaw(string rawCommand)
    {
        if (!_isConnected || _udpClient is null)
            throw new InvalidOperationException("CommandSender is not connected. Call Connect() first.");

        byte[] data = Encoding.ASCII.GetBytes(rawCommand);
        await _udpClient.SendAsync(data, data.Length);

        Info.LastCommand = rawCommand;
        Info.CommandsSent++;
        Console.WriteLine($"[CommandSender] Sent raw: {rawCommand}");
    }

    /// <summary>This block does not process pipeline data. No-op.</summary>
    protected override void OnReceive(object sender, object tNow) { }

    /// <summary>Closes the UDP client and releases resources.</summary>
    public override void Dispose()
    {
        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;
        _isConnected = false;
        Info.Status = "Disconnected";
        base.Dispose();
    }
}

/// <summary>
/// Observable telemetry model for the <see cref="CommandSender"/> block.
/// </summary>
public partial class CommandSenderInfo : ObservableObject
{
    /// <summary>Current connection status string.</summary>
    [ObservableProperty] private string _status = "Idle";

    /// <summary>Target endpoint in <c>host:port</c> format.</summary>
    [ObservableProperty] private string _target = string.Empty;

    /// <summary>The most recently sent command string.</summary>
    [ObservableProperty] private string _lastCommand = string.Empty;

    /// <summary>Running count of commands sent since connection.</summary>
    [ObservableProperty] private int _commandsSent;
}
