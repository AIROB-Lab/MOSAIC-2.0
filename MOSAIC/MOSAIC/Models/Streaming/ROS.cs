using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using static MOSAIC.Components.Basics.JsonModel;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Models.Streaming;

/// <summary>
/// Connects to a ROS bridge server (rosbridge_suite) and either publishes or subscribes
/// to a ROS topic over WebSocket (port 9090).
/// </summary>
/// <remarks>
/// <para>
/// Ensure the ROS bridge is running before starting MOSAIC:
/// <code>
/// ROS2: ros2 launch rosbridge_server rosbridge_websocket_launch.xml
/// ROS1: roslaunch rosbridge_server rosbridge_websocket.launch
/// </code>
/// </para>
/// <para>
/// <strong>Pipeline JSON example (subscribe):</strong>
/// <code>
/// "ros_in": { "Type": "RosBlock", "Inputs": [],
///   "Params": [ "192.168.1.100", "subscribe", "emg_data", "std_msgs/Float64MultiArray", "32" ] }
/// </code>
/// <strong>Pipeline JSON example (publish):</strong>
/// <code>
/// "ros_out": { "Type": "RosBlock", "Inputs": [ "processed" ],
///   "Params": [ "192.168.1.100", "publish", "prediction", "std_msgs/Float64MultiArray", "5" ] }
/// </code>
/// </para>
/// <para>
/// <strong>Thread safety:</strong> WebSocket receive loop runs on a background task.
/// All UI-affecting property changes fire <c>PropertyChanged</c> which the ViewModel
/// marshals to the UI thread.
/// </para>
/// </remarks>
public sealed partial class ROS : BaseBlock
{
    /// <summary>Source block — produces a stream and takes no inputs.</summary>
    public override int MinInputs => 0;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 0;

    #region Fields

    private ClientWebSocket?         _ws;
    private CancellationTokenSource  _cts = new();
    private long                     _messagesReceived;
    private long                     _messagesSent;

    #endregion

    #region Observable Properties

    [ObservableProperty] private string _ipAddress        = string.Empty;
    [ObservableProperty] private string _action           = string.Empty;
    [ObservableProperty] private string _topic            = string.Empty;
    [ObservableProperty] private string _messageType      = string.Empty;
    [ObservableProperty] private int    _numChannels      = 1;
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private long   _messageCount;

    #endregion

    #region Public Surface

    public bool IsPublisher  => string.Equals(_action, "publish",   StringComparison.OrdinalIgnoreCase);
    public bool IsSubscriber => string.Equals(_action, "subscribe", StringComparison.OrdinalIgnoreCase);

    public event Action<Vector>? OnMessageReceived;

    #endregion

    #region Constructor & Factory

    public ROS(
        string name,
        double desiredRate,
        string ipAddress,
        string action,
        string topic,
        string messageType,
        int    numChannels = 1)
        : base(name, desiredRate)
    {
        _ipAddress   = ipAddress;
        _action      = action;
        _topic       = topic;
        _messageType = messageType;
        _numChannels = numChannels;

        _ = ConnectAsync();
    }

    public static ROS ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var ip          = m.Params?.Count > 0 ? GetString(m.Params[0], "localhost")                        : "localhost";
        var action      = m.Params?.Count > 1 ? GetString(m.Params[1], "subscribe")                        : "subscribe";
        var topic       = m.Params?.Count > 2 ? GetString(m.Params[2], "ros_topic")                        : "ros_topic";
        var msgType     = m.Params?.Count > 3 ? GetString(m.Params[3], "std_msgs/Float64MultiArray")       : "std_msgs/Float64MultiArray";
        var numChannels = m.Params?.Count > 4 ? GetInt(m.Params[4], 1)                                     : 1;
        var rate        = m.DesiredRate ?? 200;

        return ActivatorUtilities.CreateInstance<ROS>(
            sp, m.Name, rate, ip, action, topic, msgType, numChannels);
    }

    #endregion

    #region JSON Export

    protected override string JsonTypeName => "RosBlock";

    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object> { IpAddress, Action, Topic, MessageType, NumChannels };

    #endregion

    #region Connection

    private async Task ConnectAsync()
    {
        try
        {
            _cts             = new CancellationTokenSource();
            _ws              = new ClientWebSocket();
            var uri          = new Uri($"ws://{IpAddress}:9090");

            Debug.WriteLine($"[{Name}] Connecting to ROS bridge at {uri}");
            ConnectionStatus = "Connecting…";

            await _ws.ConnectAsync(uri, _cts.Token);

            IsConnected      = true;
            ConnectionStatus = "Connected";
            Debug.WriteLine($"[{Name}] ROS bridge connected.");

            if (IsSubscriber)
                await SendJsonAsync(new { op = "subscribe", topic = $"/{Topic}", type = MessageType });
            else if (IsPublisher)
                await SendJsonAsync(new { op = "advertise", topic = $"/{Topic}", type = MessageType });

            if (IsSubscriber)
                _ = ReceiveLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Connection failed: {ex.Message}");
            IsConnected      = false;
            ConnectionStatus = $"Error: {ex.Message}";
        }
    }

    public void Reconnect()
    {
        _cts.Cancel();
        _ws?.Dispose();
        _ws              = null;
        IsConnected      = false;
        ConnectionStatus = "Reconnecting…";
        _ = ConnectAsync();
    }

    #endregion

    #region Receive Loop

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                ProcessMessage(sb.ToString());
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Receive error: {ex.Message}");
        }
        finally
        {
            IsConnected      = false;
            ConnectionStatus = "Disconnected";
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var msg = JObject.Parse(json);
            if (msg["op"]?.ToString()    != "publish")   return;
            if (msg["topic"]?.ToString() != $"/{Topic}") return;

            var dataArray = ((JArray)msg["msg"]!["data"]!).ToObject<double[]>()!;
            var vector    = ReshapeToVector(dataArray, NumChannels);

            Interlocked.Increment(ref _messagesReceived);
            MessageCount = _messagesReceived;

            Publish(vector);
            OnMessageReceived?.Invoke(vector);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Message parse error: {ex.Message}");
        }
    }

    #endregion

    #region Data Pipeline

    protected override void OnReceive(object sender, object data)
    {
        if (!IsPublisher || _ws?.State != WebSocketState.Open) return;

        try
        {
            double[] payload = data switch
            {
                Vector v => v.ToArray(),
                Matrix m => m.ToRowMajorArray(),
                _        => Array.Empty<double>()
            };

            if (payload.Length == 0) return;

            _ = SendJsonAsync(new
            {
                op    = "publish",
                topic = $"/{Topic}",
                type  = MessageType,
                msg   = new { data = payload }
            });

            Interlocked.Increment(ref _messagesSent);
            MessageCount = _messagesSent;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{Name}] Publish error: {ex.Message}");
        }
    }

    #endregion

    #region Helpers

    private async Task SendJsonAsync(object payload)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JObject.FromObject(payload).ToString());
        await _ws.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: _cts.Token);
    }

    private static Vector ReshapeToVector(double[] data, int numChannels)
    {
        if (numChannels <= 1)
            return Vector.Build.DenseOfArray(data);

        int numSamples = data.Length / numChannels;
        var matrix     = new double[numChannels, numSamples];

        for (int i = 0; i < data.Length; i++)
        {
            int row = i % numChannels;
            int col = i / numChannels;
            if (row < numChannels && col < numSamples)
                matrix[row, col] = data[i];
        }

        return Vector.Build.DenseOfArray(matrix.Cast<double>().ToArray());
    }

    #endregion

    #region Dispose

    public override void Dispose()
    {
        _cts.Cancel();
        _ws?.Dispose();
        _ws = null;
        base.Dispose();
    }

    #endregion
}