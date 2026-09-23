using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.Models.Streaming;

/// <summary>
/// Sends control values over UDP to a Blender arm model for real-time 3D visualisation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> Converts the <see cref="DegreesOfActuation"/> dictionary from an upstream
/// <see cref="MOSAIC.Models.FlowControl.ControlAlgorithm"/> into a 9-element vector and sends
/// it as an ASCII-encoded JSON array over UDP to a Blender Python script (<c>3DHands_left.py</c>).
/// </para>
/// <para>
/// <b>Vector layout:</b>
/// <code>
/// [ th_flex, th_rot, index, middle, ring, little, wr_flex_ext, wr_uln_rad, wr_sup_pron ]
///   [0..1]   [0..1]  [0..1] [0..1]  [0..1] [0..1]  [-1..1]     [-1..1]     [-1..1]
/// </code>
/// Finger values are scaled from [0, 100] → [0, 1].
/// Wrist values are scaled from [0, 100] → [−1, 1].
/// Wrist pronation/supination is additionally negated.
/// </para>
/// <para>
/// <b>Connection:</b> Call <see cref="Connect"/> to open the UDP socket. The block silently
/// drops data if not connected. Call <see cref="Disconnect"/> or <see cref="Reconnect"/>
/// to manage the socket lifecycle.
/// </para>
/// <para>
/// <b>CSV logging:</b> This block publishes a <c>Dictionary&lt;DegreesOfActuation, double&gt;</c>
/// which <see cref="CsvDumper"/> does not support. The 9-element command vector is published
/// for downstream consumption; logging should be configured on the upstream ControlAlgorithm
/// or a downstream block that receives the vector.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "Blender": {
///     "Type": "BlenderArm",
///     "Inputs": [ "ControlAlgorithm" ],
///     "Params": [ "127.0.0.1", 5005 ]
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description><c>HostIp</c> (string) — target UDP host IP address.</description></item>
///   <item><term>1</term><description><c>Port</c> (int) — target UDP port number.</description></item>
/// </list>
/// </para>
/// </example>
public sealed partial class BlenderArm : BaseBlock
{
    /// <summary>UDP client for sending command strings to Blender.</summary>
    private UdpClient _udpClient = new();

    /// <summary>Preallocated 9-element command vector.</summary>
    private Vector _blenderArmCmd = Vector.Build.Dense(9);

    /// <summary>Target host IP address.</summary>
    [ObservableProperty] private string _hostIp;

    /// <summary>Target UDP port.</summary>
    [ObservableProperty] private int _port;

    /// <summary>Whether the UDP connection has been established.</summary>
    [ObservableProperty] private bool _isConnected;

    /// <summary>Total messages sent.</summary>
    [ObservableProperty] private long _messageCount;

    /// <summary>Last command string sent (for UI display).</summary>
    [ObservableProperty] private string _lastCommand = "—";

    /// <summary>Current thumb flexion value [0, 1].</summary>
    [ObservableProperty] private double _thumbFlexion;
    /// <summary>Current thumb rotation value [0, 1].</summary>
    [ObservableProperty] private double _thumbRotation;
    /// <summary>Current index finger value [0, 1].</summary>
    [ObservableProperty] private double _index;
    /// <summary>Current middle finger value [0, 1].</summary>
    [ObservableProperty] private double _middle;
    /// <summary>Current ring finger value [0, 1].</summary>
    [ObservableProperty] private double _ring;
    /// <summary>Current little finger value [0, 1].</summary>
    [ObservableProperty] private double _little;
    /// <summary>Current wrist flexion/extension value [−1, 1].</summary>
    [ObservableProperty] private double _wristFlexExt;
    /// <summary>Current wrist ulnar/radial value [−1, 1].</summary>
    [ObservableProperty] private double _wristUlnRad;
    /// <summary>Current wrist supination/pronation value [−1, 1].</summary>
    [ObservableProperty] private double _wristSupPron;

    /// <summary>
    /// Optional visualization bundle. Set by the ViewModel so the block can feed
    /// data to all monitors in one call.
    /// </summary>
    private BlockVisualization? _viz;

    /// <summary>Live plot for this block. Assigned by the ViewModel, which owns the scope.</summary>
    /// <remarks>
    /// Setting this re-pushes the publish rate: the rate is normally pushed when it changes,
    /// which for these blocks happens during construction — before the ViewModel has handed
    /// over the scope — so without this the scope would never learn its time base.
    /// </remarks>
    public BlockVisualization? Viz
    {
        get => _viz;
        set { _viz = value; RefreshVisualizationRate(); }
    }

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>
    /// Initializes a new <see cref="BlenderArm"/> block.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="desiredRate">Processing rate in Hz (typically inherited).</param>
    /// <param name="hostIp">Target UDP host IP address.</param>
    /// <param name="port">Target UDP port number.</param>
    public BlenderArm(string name = "BlenderArm", double desiredRate = 0,
                      string hostIp = "127.0.0.1", int port = 5005)
        : base(name, desiredRate)
    {
        _hostIp = hostIp;
        _port = port;
    }

    /// <summary>
    /// Creates a <see cref="BlenderArm"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// JSON model. See the class-level example for <c>Params</c> layout.
    /// </param>
    /// <returns>A configured <see cref="BlenderArm"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown if required parameters are missing.</exception>
    public static BlenderArm ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "BlenderArm";
        var rate = m.DesiredRate ?? 0;

        // if (m.Params is not { Count: >= 2 })
        //     throw new ArgumentException($"Block {name} must have at least 2 parameters [hostIP, port]");

        var hostIp = m.Params?[0]?.ToString() ?? "127.0.0.1";
        if (!int.TryParse(m.Params?[1]?.ToString() ?? "3334", out var port))
            throw new ArgumentException($"Block {name}: Params[1] must be a valid port number");

        return ActivatorUtilities.CreateInstance<BlenderArm>(sp, name, rate, hostIp, port);
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "BlenderArm";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
        => new object[] { HostIp, Port };

    #endregion

    /// <summary>Opens the UDP socket and connects to <see cref="HostIp"/>:<see cref="Port"/>.</summary>
    public void Connect()
    {
        if (IsConnected) return;

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Connect(HostIp, Port);
            IsConnected = true;
            Debug.WriteLine($"[{Name}] Blender UDP connected → {HostIp}:{Port}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Blender UDP connect failed: {ex.Message}");
            IsConnected = false;
        }
    }

    /// <summary>Closes and disposes the UDP socket.</summary>
    public void Disconnect()
    {
        try { _udpClient.Close(); _udpClient.Dispose(); } catch { /* no-op */ }
        IsConnected = false;
        Debug.WriteLine($"[{Name}] Blender UDP disconnected");
    }

    /// <summary>Disconnects and reconnects the UDP socket.</summary>
    public void Reconnect()
    {
        Disconnect();
        Connect();
    }

    /// <summary>
    /// Disposes visualization, disconnects UDP, and releases base class resources.
    /// </summary>
    public override void Dispose()
    {
        try { Viz?.Dispose(); } catch { /* no-op */ }
        Disconnect();
        base.Dispose();
    }

    /// <summary>
    /// Converts the incoming DOA dictionary to a 9-element command vector,
    /// sends it over UDP, and publishes it downstream.
    /// </summary>
    /// <param name="sender">The upstream block (typically ControlAlgorithm).</param>
    /// <param name="data">
    /// Expected to be a <c>Dictionary&lt;DegreesOfActuation, double&gt;</c>.
    /// Other types are silently ignored. Data is also dropped if not connected.
    /// </param>
    protected override void OnReceive(object sender, object data)
    {
        if (!IsConnected) return;
        if (data is not Dictionary<DegreesOfActuation, double> actuationDict) return;

        // Fingers: [0,100] → [0,1]
        _blenderArmCmd[0] = GetDoa(actuationDict, DegreesOfActuation.ThumbFlexion) / 100.0;
        _blenderArmCmd[1] = GetDoa(actuationDict, DegreesOfActuation.ThumbRotation) / 100.0;
        _blenderArmCmd[2] = GetDoa(actuationDict, DegreesOfActuation.Index) / 100.0;
        _blenderArmCmd[3] = GetDoa(actuationDict, DegreesOfActuation.Middle) / 100.0;
        _blenderArmCmd[4] = GetDoa(actuationDict, DegreesOfActuation.Ring) / 100.0;
        _blenderArmCmd[5] = GetDoa(actuationDict, DegreesOfActuation.Little) / 100.0;

        // Wrist: [0,100] → [-1,1]
        _blenderArmCmd[6] = GetDoa(actuationDict, DegreesOfActuation.WristFlexionExtension) / 50.0 - 1.0;
        _blenderArmCmd[7] = GetDoa(actuationDict, DegreesOfActuation.WristUlnarRadial) / 50.0 - 1.0;
        _blenderArmCmd[8] = -1.0 * (GetDoa(actuationDict, DegreesOfActuation.WristPronationSupination) / 50.0 - 1.0);

        // Update observable properties for UI
        ThumbFlexion = _blenderArmCmd[0];
        ThumbRotation = _blenderArmCmd[1];
        Index = _blenderArmCmd[2];
        Middle = _blenderArmCmd[3];
        Ring = _blenderArmCmd[4];
        Little = _blenderArmCmd[5];
        WristFlexExt = _blenderArmCmd[6];
        WristUlnRad = _blenderArmCmd[7];
        WristSupPron = _blenderArmCmd[8];

        var sb = new StringBuilder(64);
        sb.Append('[');
        for (int i = 0; i < _blenderArmCmd.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.AppendFormat("{0:F4}", _blenderArmCmd[i]);
        }
        sb.Append(']');

        string sendString = sb.ToString();
        LastCommand = sendString;
        MessageCount++;

        byte[] bytes = Encoding.ASCII.GetBytes(sendString);
        _udpClient.SendAsync(bytes, bytes.Length);

        var output = _blenderArmCmd.Clone();
        Viz?.Feed(output);
        Publish(output);
    }

    /// <summary>
    /// Safely retrieves a DOA value from the dictionary, returning 0 if not present.
    /// </summary>
    private static double GetDoa(Dictionary<DegreesOfActuation, double> dict, DegreesOfActuation key)
        => dict.TryGetValue(key, out var val) ? val : 0;
}
