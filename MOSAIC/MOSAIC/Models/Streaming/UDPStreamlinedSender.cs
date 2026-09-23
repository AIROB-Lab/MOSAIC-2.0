using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization;
using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.Models.Streaming;

/// <summary>
/// UDP sender implementing Unity's Streamlined Input Manager binary protocol.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> Sends structured binary messages over UDP with a standardised header.
/// The block dispatches on the upstream sender's type name to determine the data category
/// and serialisation format.
/// </para>
/// <para>
/// <b>Packet layout:</b>
/// <code>
/// | 0: count | 1: value type | 2: data type | 3: data subtype | 4–11: timestamp | 12…N-2: DATA | N-2…N-1: counter (UInt16) |
/// </code>
/// </para>
/// <para>
/// <b>Data type / subtype combinations:</b>
/// <list type="bullet">
///   <item><description><c>[0,0]</c> EMG raw, <c>[0,1]</c> EMG ARV.</description></item>
///   <item><description><c>[1,x]</c> PositionControl — subtype is <see cref="DegreesOfActuation"/> enum value.</description></item>
///   <item><description><c>[2,0]</c> FMG raw, <c>[2,1]</c> FMG ARV.</description></item>
///   <item><description><c>[3,x]</c> PositionControl2 — bimanual, same subtypes as [1,x].</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Sender dispatch:</b> The block uses the upstream block's type name to select the
/// serialisation path: <c>"ControlAlgorithm"</c> / <c>"ManualControl"</c> → per-DOA double
/// messages, <c>"Myo"</c> → sbyte EMG raw, <c>"Resampler"</c> → double EMG ARV,
/// anything else with a vector → generic FMG raw as doubles.
/// </para>
/// <para>
/// <b>Stream size:</b> Upstream blocks control the stream size by choosing which DOAs to
/// include in the published dictionary. Any DOA not in the dict is simply not serialised and
/// no packet is sent for it. <see cref="MOSAIC.Models.FlowControl.ManualControl"/> exposes
/// group gates (fingers / wrist / elbow) for exactly this purpose.
/// </para>
/// <para>
/// <b>CSV logging:</b> This block publishes vectors for downstream consumption.
/// When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/> automatically logs
/// published vectors via <see cref="BaseBlock.Publish"/>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "UnitySender": {
///     "Type": "UdpStreamlinedSender",
///     "Inputs": [ "ControlAlgorithm", "Myo", "Resampler" ],
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
public sealed partial class UdpStreamlinedSender : BaseBlock
{
    /// <summary>UDP client for sending to Unity.</summary>
    private UdpClient _udpClient = new();

    /// <summary>Supported floating-point types for the binary protocol.</summary>
    private static readonly Type[] FloatingTypes = { typeof(float), typeof(double) };

    /// <summary>Supported signed integer types for the binary protocol.</summary>
    private static readonly Type[] SignedTypes = { typeof(sbyte), typeof(short), typeof(int), typeof(long) };

    /// <summary>Supported unsigned integer types for the binary protocol.</summary>
    private static readonly Type[] UnsignedTypes = { typeof(ushort), typeof(uint), typeof(ulong), typeof(byte) };

    /// <summary>Target host IP address.</summary>
    [ObservableProperty] private string _hostIp;

    /// <summary>Target UDP port.</summary>
    [ObservableProperty] private int _port;

    /// <summary>Whether the UDP connection has been established.</summary>
    [ObservableProperty] private bool _isConnected;

    /// <summary>Rolling UDP message counter (wraps at UInt16 max).</summary>
    [ObservableProperty] private ushort _udpCounter;

    /// <summary>Total bytes sent since connection.</summary>
    [ObservableProperty] private long _totalBytesSent;

    /// <summary>Last data type category sent (for UI display).</summary>
    [ObservableProperty] private string _lastDataType = "—";

    /// <summary>Last sender block type name (for UI display).</summary>
    [ObservableProperty] private string _lastSenderType = "—";

    /// <summary>Rolling log of recent messages (newest first, max 50).</summary>
    public ObservableCollection<string> MessageLog { get; } = new();

    /// <summary>Maximum entries in the message log before oldest are trimmed.</summary>
    private const int MaxLogEntries = 50;

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
    /// Initializes a new <see cref="UdpStreamlinedSender"/> block.
    /// </summary>
    public UdpStreamlinedSender(string name = "UdpSender", double desiredRate = 0,
                                string hostIp = "127.0.0.1", int port = 5005)
        : base(name, desiredRate)
    {
        _hostIp = hostIp;
        _port = port;
    }

    /// <summary>
    /// Creates a <see cref="UdpStreamlinedSender"/> from a JSON pipeline definition.
    /// </summary>
    public static UdpStreamlinedSender ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "UdpSender";
        var rate = m.DesiredRate ?? 0;

        // if (m.Params is not { Count: >= 2 })
        //     throw new ArgumentException($"Block {name} must have at least 2 parameters [hostIP, port]");

        var hostIp = m.Params?[0]?.ToString() ?? "127.0.0.1";
        if (!int.TryParse(m.Params?[1]?.ToString() ?? "8080", out var port))
            throw new ArgumentException($"Block {name}: Params[1] must be a valid port number");

        return ActivatorUtilities.CreateInstance<UdpStreamlinedSender>(sp, name, rate, hostIp, port);
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "UdpStreamlinedSender";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
        => new object[] { HostIp, Port };

    #endregion

    /// <summary>Opens the UDP connection to <see cref="HostIp"/>:<see cref="Port"/>.</summary>
    public void Connect()
    {
        if (IsConnected) return;

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Connect(HostIp, Port);
            IsConnected = true;
            Debug.WriteLine($"[{Name}] UDP connected → {HostIp}:{Port}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] UDP connect failed: {ex.Message}");
            IsConnected = false;
        }
    }

    /// <summary>Closes and disposes the UDP client.</summary>
    public void Disconnect()
    {
        try { _udpClient.Close(); _udpClient.Dispose(); } catch { /* no-op */ }
        IsConnected = false;
        Debug.WriteLine($"[{Name}] UDP disconnected");
    }

    /// <summary>Disconnects and reconnects (with potentially updated host/port).</summary>
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
    /// Dispatches incoming data based on the upstream sender type and serialises
    /// it using the Streamlined Input Manager binary protocol.
    /// </summary>
    protected override void OnReceive(object sender, object data)
    {
        if (!IsConnected) return;

        List<byte> dataList;
        var senderTypeName = sender.GetType().Name;
        LastSenderType = senderTypeName;

        switch (senderTypeName)
        {
            case "ControlAlgorithm":
            case "ControlAlgorithm2":
            case "ManualControl":
            {
                if (data is not Dictionary<DegreesOfActuation, double> actuationDict) return;

                byte category = (byte)(senderTypeName == "ControlAlgorithm2" ? 3 : 1);
                LastDataType = category == 1 ? "CTRL" : "CTRL2";

                foreach (var item in actuationDict)
                {
                    dataList = new List<byte>();
                    byte[] dataType = { category, (byte)item.Key };
                    dataList.AddRange(BitConverter.GetBytes(item.Value));
                    SendUdpMessage(1, typeof(double), dataType, dataList.ToArray());
                }

                if (actuationDict.Count > 0)
                {
                    var values = Vector.Build.DenseOfEnumerable(actuationDict.Values);
                    Viz?.Feed(values);
                    Publish(values);
                }

                break;
            }

            case "Myo":
            {
                if (data is not Vector valVec) return;
                LastDataType = "EMG Raw";

                dataList = new List<byte>();
                for (int i = 0; i < valVec.Count; i++)
                    dataList.Add((byte)(128 * valVec[i]));

                SendUdpMessage((byte)dataList.Count, typeof(sbyte), DataType.EMG_RAW, dataList.ToArray());
                Viz?.Feed(valVec);
                Publish(valVec);
                break;
            }

            case "Resampler":
            {
                if (data is not Vector valVec) return;
                LastDataType = "EMG ARV";

                dataList = new List<byte>();
                for (int i = 0; i < valVec.Count; i++)
                    dataList.AddRange(BitConverter.GetBytes(valVec[i]));

                SendUdpMessage((byte)valVec.Count, typeof(double), DataType.EMG_ARV, dataList.ToArray());
                Viz?.Feed(valVec);
                Publish(valVec);
                break;
            }

            default:
            {
                if (data is Vector vec)
                {
                    LastDataType = "Generic";

                    dataList = new List<byte>();
                    for (int i = 0; i < vec.Count; i++)
                        dataList.AddRange(BitConverter.GetBytes(vec[i]));

                    SendUdpMessage((byte)vec.Count, typeof(double), DataType.FMG_RAW, dataList.ToArray());
                    Viz?.Feed(vec);
                    Publish(vec);
                }

                break;
            }
        }
    }

    /// <summary>Returns the protocol byte encoding for a numeric data type.</summary>
    private static byte GetByteForType(Type type)
    {
        if (!FloatingTypes.Contains(type) && !SignedTypes.Contains(type) && !UnsignedTypes.Contains(type))
            throw new NotSupportedException($"Data type not supported by UDP protocol: {type.Name}");

        byte typeInfo = 0b_0000_0000;

        if (FloatingTypes.Contains(type))
            typeInfo += 0b_0010_0000;
        else if (SignedTypes.Contains(type))
            typeInfo += 0b_0001_0000;

        typeInfo += (byte)Marshal.SizeOf(type);
        return typeInfo;
    }

    /// <summary>Sends a standardised UDP message with the protocol header.</summary>
    private void SendUdpMessage(byte numCount, Type valType, byte[] dataType, byte[] data)
    {
        var udpMessage = new List<byte>(4 + 8 + data.Length + 2)
        {
            numCount,
            GetByteForType(valType),
            dataType[0],
            dataType[1],
        };

        udpMessage.AddRange(BitConverter.GetBytes(TickTracker.Now()));
        udpMessage.AddRange(data);
        udpMessage.AddRange(BitConverter.GetBytes(UdpCounter));

        UdpCounter++;
        TotalBytesSent += udpMessage.Count;

        _udpClient.SendAsync(udpMessage.ToArray(), udpMessage.Count);

        if (UdpCounter % 100 == 0)
        {
            var entry = $"#{UdpCounter} [{dataType[0]}.{dataType[1]}] {numCount}× {valType.Name} ({udpMessage.Count}B)";
            if (MessageLog.Count >= MaxLogEntries)
                MessageLog.RemoveAt(MessageLog.Count - 1);
            MessageLog.Insert(0, entry);
        }
    }
}

/// <summary>
/// Static data type constants for the Streamlined Input Manager binary protocol.
/// </summary>
internal static class DataType
{
    /// <summary>EMG raw signal [0,0].</summary>
    public static readonly byte[] EMG_RAW = { 0, 0 };
    /// <summary>EMG average rectified value [0,1].</summary>
    public static readonly byte[] EMG_ARV = { 0, 1 };

    /// <summary>FMG raw signal [2,0].</summary>
    public static readonly byte[] FMG_RAW = { 2, 0 };
    /// <summary>FMG average rectified value [2,1].</summary>
    public static readonly byte[] FMG_ARV = { 2, 1 };

    /// <summary>Returns a human-readable label for a data type/subtype pair.</summary>
    public static string GetLabel(byte category, byte subtype) => category switch
    {
        0 => subtype switch { 0 => "EMG Raw", 1 => "EMG ARV", _ => $"EMG.{subtype}" },
        1 => $"CTRL {(DegreesOfActuation)subtype}",
        2 => subtype switch { 0 => "FMG Raw", 1 => "FMG ARV", _ => $"FMG.{subtype}" },
        3 => $"CTRL2 {(DegreesOfActuation)subtype}",
        _ => $"{category}.{subtype}"
    };

    /// <summary>Returns all known protocol entries for UI display.</summary>
    public static IReadOnlyList<(byte Cat, byte Sub, string Label)> GetAllEntries() => new (byte, byte, string)[]
    {
        (0, 0, "EMG Raw"),
        (0, 1, "EMG ARV"),
        (1, (byte)DegreesOfActuation.ThumbFlexion,              "CTRL ThumbFlexion"),
        (1, (byte)DegreesOfActuation.ThumbRotation,             "CTRL ThumbRotation"),
        (1, (byte)DegreesOfActuation.Index,                     "CTRL Index"),
        (1, (byte)DegreesOfActuation.Middle,                    "CTRL Middle"),
        (1, (byte)DegreesOfActuation.Ring,                      "CTRL Ring"),
        (1, (byte)DegreesOfActuation.Little,                    "CTRL Little"),
        (1, (byte)DegreesOfActuation.WristFlexionExtension,     "CTRL WristFlexExt"),
        (1, (byte)DegreesOfActuation.WristUlnarRadial,          "CTRL WristUlnRad"),
        (1, (byte)DegreesOfActuation.WristPronationSupination,  "CTRL WristSupPron"),
        (1, (byte)DegreesOfActuation.HandOpenClose,             "CTRL HandOpenClose"),
        (1, (byte)DegreesOfActuation.ElbowFlexion,              "CTRL ElbowFlexion"),
        (1, (byte)DegreesOfActuation.ElbowExtension,            "CTRL ElbowExtension"),
        (2, 0, "FMG Raw"),
        (2, 1, "FMG ARV"),
        (3, (byte)DegreesOfActuation.ThumbFlexion,              "CTRL2 ThumbFlexion (bimanual)"),
    };
}