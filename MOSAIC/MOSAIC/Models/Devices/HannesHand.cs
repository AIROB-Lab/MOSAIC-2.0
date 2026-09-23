using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Devices.HannesHand;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization.ScopeMonitor;
using static MOSAIC.Components.Devices.HannesHand.HannesProtocol;

namespace MOSAIC.Models.Devices;

/// <summary>
/// Internal connection/streaming state of the Hannes prosthesis.
/// </summary>
public enum DeviceStatus
{
    /// <summary>No connection attempted yet.</summary>
    Unknown,

    /// <summary>Serial dongle is open and in command mode.</summary>
    DongleReady,

    /// <summary>BLE connection established and streaming configured.</summary>
    Ready,

    /// <summary>An error occurred during connection or communication.</summary>
    Error
}

/// <summary>
/// Hannes robotic prosthesis control block — sends 4-DOF joint references over a BLE-to-serial dongle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> Receives a 4-element <see cref="Vector{T}"/> of <see cref="double"/> (each 0–1)
/// representing normalised joint references, converts them to the Hannes EMGEM protocol format,
/// and transmits them over a BLE dongle serial link. Also supports a manual slider mode for
/// direct UI control.
/// </para>
///
/// <para>
/// <b>Input vector layout:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Joint</description></listheader>
///   <item><term>0</term><description>Hand open/close (0 = open, 1 = closed)</description></item>
///   <item><term>1</term><description>Wrist flexion/extension (0 = extension, 1 = flexion)</description></item>
///   <item><term>2</term><description>Wrist pronation/supination (0 = supination, 0.5 = neutral, 1 = pronation)</description></item>
///   <item><term>3</term><description>Thumb rotation (0 = one extreme, 0.5 = neutral, 1 = other extreme)</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Input types:</b> The block accepts three input shapes:
/// <list type="bullet">
///   <item><description><see cref="Vector{T}"/> of <see cref="double"/> — used directly.</description></item>
///   <item><description><c>Dictionary&lt;DegreesOfActuation, double&gt;</c> — converted via <see cref="ConvertFromDoaDict"/>.</description></item>
///   <item><description><c>ValueTuple&lt;string, object&gt;</c> — tagged wrapper; inner value is unwrapped and handled as above.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Connection lifecycle:</b>
/// <list type="number">
///   <item><description><see cref="ConnectDongleAsync"/> — opens the COM port and enters dongle command mode.</description></item>
///   <item><description><see cref="ScanForDevicesAsync"/> — sends a BLE scan command and parses discovered devices.</description></item>
///   <item><description><see cref="ConnectHannesAsync"/> — connects to the selected device, pings for ACK, and configures reference control mode.</description></item>
///   <item><description><see cref="DisconnectHannes"/> — switches to command mode, disconnects BLE, and closes the link.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>EMGEM protocol:</b> Packets are framed as <c>0x25 (%) | payload | 0x26 (&amp;) | CR | LF</c>.
/// The <see cref="SendSimultaneousRefs"/> method builds an 11-byte payload containing joint references
/// scaled and sign-converted per the Hannes firmware specification.
/// </para>
///
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified in the JSON configuration, the base class
/// <see cref="BaseBlock.Dumper"/> automatically logs every published vector as
/// <c>timestamp, hand, wristFE, wristPS, thumb</c>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "MyHannes": {
///     "Type": "HannesHand",
///     "DesiredRate": 50,
///     "Inputs": [ "ControlAlgorithm" ],
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b> None. Port name and device selection are configured via the UI at runtime.
/// </para>
/// </example>
public sealed class HannesHand : BaseBlock
{

    #region Fields

    /// <summary>Serial dongle client for Hannes EMGEM communication.</summary>
    private readonly RehabDongleClient _dongle = new();

    /// <summary>Current dongle streaming mode (command vs. stream).</summary>
    private StreamMode _mode = StreamMode.Stream;

    /// <summary>Whether BLE is connected to the prosthesis.</summary>
    private bool _connected;

    /// <summary>Whether streaming/reference-control mode is active.</summary>
    private bool _streaming;

    /// <summary>Current device connection state.</summary>
    private DeviceStatus _status = DeviceStatus.Unknown;

    // ── BLE scan results ──

    /// <summary>Device display names from the last BLE scan.</summary>
    private readonly List<string> _names = new();

    /// <summary>Connection indices from the last BLE scan (used by <c>con {id}</c> command).</summary>
    private readonly List<int> _cons = new();

    /// <summary>RSSI values from the last BLE scan.</summary>
    private readonly List<int> _rssis = new();

    /// <summary>BLE MAC addresses from the last BLE scan (used for deduplication).</summary>
    private readonly List<string> _addrs = new();

    #endregion
    
    #region Properties
    /// <summary>
    /// Scope for visualising the 4-channel joint reference output in the UI.
    /// </summary>
    public ScopeMonitor Scope { get; } = new();

    /// <summary>
    /// Serial port name for the BLE dongle (e.g. <c>"COM3"</c>). Set by the ViewModel.
    /// </summary>
    public string? PortName { get; set; }

    /// <summary>
    /// Display name of the BLE device selected from <see cref="ScanForDevicesAsync"/> results.
    /// Set by the ViewModel.
    /// </summary>
    public string? SelectedDevice { get; set; }

    /// <summary>Enables/disables wrist flexion/extension DOF in reference control.</summary>
    public bool WristExFlex { get; set; } = true;

    /// <summary>Enables/disables wrist pronation/supination DOF in reference control.</summary>
    public bool WristSupPro { get; set; } = true;

    /// <summary>Enables/disables hand open/close DOF in reference control.</summary>
    public bool HandOpenClose { get; set; } = true;

    /// <summary>Enables/disables thumb rotation DOF in reference control.</summary>
    public bool Thumb { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, ignores pipeline input and uses manual slider values
    /// via <see cref="SendManualRefs"/>.
    /// </summary>
    public bool IsManualMode { get; set; }
    
    #endregion

    /// <summary>
    /// Initializes a new <see cref="HannesHand"/> block.
    /// </summary>
    /// <param name="name">Display name of the block.</param>
    /// <param name="desiredRate">
    /// Desired processing rate in Hz. Typical values: 20–50 Hz for prosthesis control.
    /// </param>
    public HannesHand(string name = "HannesHand", double desiredRate = 0)
        : base(name, desiredRate)
    {
        Scope.ScopeInit(0);
        Debug.WriteLine($"[HannesHand] Created: {name}, rate={desiredRate}");
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_out.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_out";

    /// <summary>
    /// Creates and configures a <see cref="HannesHand"/> from a JSON model definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">The JSON model. See the class-level example for details.</param>
    /// <returns>A fully configured <see cref="HannesHand"/> instance.</returns>
    public static HannesHand ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "HannesHand";
        var rate = m.DesiredRate ?? 0;

        Debug.WriteLine($"[HannesHand] ConfigureInput: {name}");

        var block = ActivatorUtilities.CreateInstance<HannesHand>(sp, name, rate);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "HannesHand";

    #endregion
    

    /// <summary>
    /// Releases all resources: scope, serial dongle, and base class resources
    /// (including the CSV dumper if configured).
    /// </summary>
    public override void Dispose()
    {
        base.Dispose();
        _dongle.Dispose();
    }
    

    /// <summary>
    /// Processes incoming data: extracts a 4-element reference vector and sends it to the prosthesis.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="data">
    /// One of: <see cref="Vector{T}"/> of <see cref="double"/>,
    /// <c>Dictionary&lt;DegreesOfActuation, double&gt;</c>, or a tagged
    /// <c>ValueTuple&lt;string, object&gt;</c> wrapping either of the above.
    /// </param>
    /// <remarks>
    /// Ignored when <see cref="IsManualMode"/> is <see langword="true"/> — use
    /// <see cref="SendManualRefs"/> instead.
    /// </remarks>
    protected override void OnReceive(object sender, object data)
    {
        if (IsManualMode)
            return;

        Vector<double>? refs = null;

        switch (data)
        {
            case Vector<double> v:
                refs = v;
                break;

            case Dictionary<DegreesOfActuation, double> dict:
                refs = ConvertFromDoaDict(dict);
                break;

            case ValueTuple<string, object> tagged:
                if (tagged.Item2 is Vector<double> tv)
                    refs = tv;
                else if (tagged.Item2 is Dictionary<DegreesOfActuation, double> td)
                    refs = ConvertFromDoaDict(td);
                break;
        }

        if (refs is null)
        {
            Debug.WriteLine($"[HannesHand] OnReceive: unexpected data type {data?.GetType().Name}");
            return;
        }

        SendRefs(refs);
    }

    /// <summary>
    /// Sends manual reference values from UI sliders. Only effective when
    /// <see cref="IsManualMode"/> is <see langword="true"/>.
    /// </summary>
    /// <param name="refs">Vector [Hand, WristFE, WristPS, Thumb], each 0–1.</param>
    public void SendManualRefs(Vector<double> refs)
    {
        if (!IsManualMode)
            return;

        SendRefs(refs);
    }

    /// <summary>
    /// Internal method that sends references to the device, publishes downstream, and updates the scope.
    /// </summary>
    /// <param name="refs">4-element normalised reference vector.</param>
    /// <remarks>
    /// The vector is sent to the prosthesis (if connected and streaming), then published
    /// downstream. <see cref="BaseBlock.Publish"/> handles automatic CSV logging.
    /// </remarks>
    private void SendRefs(Vector<double> refs)
    {
        if (_streaming && _connected && _dongle.IsOpen && _mode == StreamMode.Stream)
            SendSimultaneousRefs(refs);

        Publish(refs);
        Scope.EnqueueData(refs);
    }
    
    /// <summary>
    /// Converts a <c>Dictionary&lt;DegreesOfActuation, double&gt;</c> (0–100 scale) to the
    /// Hannes 4-element reference vector (0–1 scale).
    /// </summary>
    /// <param name="dict">DOA dictionary with values in the range 0–100.</param>
    /// <returns>
    /// Dense vector: [Hand (0–1), WristFE (0–1), WristPS (0–1), Thumb (0–1)].
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Hand:</b> Computed as the average of available finger flexions
    /// (Index, Middle, Ring, Little), or 0.5 if none are present.
    /// </para>
    /// <para>
    /// <b>Wrist F/E:</b> Direct mapping — 0 = full extension, 100 = full flexion.
    /// </para>
    /// <para>
    /// <b>Wrist P/S:</b> 0 = full supination, 50 = neutral, 100 = full pronation.
    /// </para>
    /// <para>
    /// <b>Thumb:</b> Uses <c>ThumbFlexion</c> DOA directly.
    /// </para>
    /// </remarks>
    private static Vector<double> ConvertFromDoaDict(Dictionary<DegreesOfActuation, double> dict)
    {
        static double Get(Dictionary<DegreesOfActuation, double> d, DegreesOfActuation key)
            => d.TryGetValue(key, out var v) ? v / 100.0 : 0.5;

        double hand = 0.5;
        if (dict.ContainsKey(DegreesOfActuation.Index) || dict.ContainsKey(DegreesOfActuation.Middle))
        {
            var fingers = new[]
            {
                DegreesOfActuation.Index,
                DegreesOfActuation.Middle,
                DegreesOfActuation.Ring,
                DegreesOfActuation.Little
            };
            double sum = 0;
            int count = 0;
            foreach (var f in fingers)
            {
                if (dict.TryGetValue(f, out var fv))
                {
                    sum += fv;
                    count++;
                }
            }
            if (count > 0) hand = (sum / count) / 100.0;
        }

        double wristFE = 0.5;
        if (dict.TryGetValue(DegreesOfActuation.WristFlexionExtension, out var wfe))
            wristFE = wfe / 100.0;

        double wristPS = 0.5;
        if (dict.TryGetValue(DegreesOfActuation.WristPronationSupination, out var wps))
            wristPS = wps / 100.0;

        double thumb = Get(dict, DegreesOfActuation.ThumbFlexion);

        return Vector<double>.Build.Dense(new[] { hand, wristFE, wristPS, thumb });
    }
    

    /// <summary>
    /// Opens the serial port and enters dongle command mode.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns><see langword="true"/> if the dongle is ready; <see langword="false"/> on failure.</returns>
    /// <remarks>
    /// If <see cref="PortName"/> is a bare number (e.g. <c>"3"</c>), it is auto-corrected
    /// to <c>"COM3"</c> on Windows.
    /// </remarks>
    public async Task<bool> ConnectDongleAsync(CancellationToken ct = default)
    {
        Debug.WriteLine($"[HannesHand] ConnectDongleAsync: PortName='{PortName}'");

        if (string.IsNullOrWhiteSpace(PortName))
        {
            Debug.WriteLine($"[HannesHand] FAILED: PortName is empty!");
            return false;
        }

        var portName = PortName!.Trim();
        if (int.TryParse(portName, out _))
        {
            portName = $"COM{portName}";
            Debug.WriteLine($"[HannesHand] Auto-corrected to '{portName}'");
        }

        try
        {
            Debug.WriteLine($"[HannesHand] Opening port '{portName}'...");
            _dongle.Open(portName);
            await Task.Delay(500, ct);

            Debug.WriteLine($"[HannesHand] Entering command mode ($$$)...");
            _dongle.EnterDongleCommandMode();
            await Task.Delay(500, ct);

            _status = DeviceStatus.DongleReady;
            Debug.WriteLine($"[HannesHand] Dongle ready!");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HannesHand] FAILED: {ex.GetType().Name} - {ex.Message}");
            _status = DeviceStatus.Error;
            return false;
        }
    }

    /// <summary>
    /// Sends a BLE scan command and returns the list of discovered device names.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A read-only list of discovered device names.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the serial port is not open (call <see cref="ConnectDongleAsync"/> first).
    /// </exception>
    public async Task<IReadOnlyList<string>> ScanForDevicesAsync(CancellationToken ct = default)
    {
        Debug.WriteLine($"[HannesHand] === ScanForDevicesAsync ===");
        EnsureOpen();

        _names.Clear();
        _cons.Clear();
        _rssis.Clear();
        _addrs.Clear();

        Debug.WriteLine($"[HannesHand] Sending 'scan' command...");
        _dongle.ClearInput();
        _dongle.WriteLine("scan");

        Debug.WriteLine($"[HannesHand] Waiting 3 seconds for scan...");
        await Task.Delay(3000, ct);

        var lines = _dongle.ReadAllLinesWithTimeout(2000);
        Debug.WriteLine($"[HannesHand] Received {lines.Length} lines");

        foreach (var line in lines)
            Debug.WriteLine($"[HannesHand]   > '{line.TrimEnd()}'");

        ParseScan(lines);

        Debug.WriteLine($"[HannesHand] Found {_names.Count} devices: [{string.Join(", ", _names)}]");
        return _names;
    }

    /// <summary>
    /// Connects to the selected Hannes device over BLE, pings for ACK, and configures
    /// reference control mode.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns><see langword="true"/> if the prosthesis is ready for streaming; <see langword="false"/> otherwise.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the serial port is not open.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Sequence:</b> <c>con {id}</c> → ping (<c>EnterCMDMode</c>) → check ACK →
    /// configure joint mask and control mode → switch to stream mode.
    /// </para>
    /// <para>
    /// The ACK reply is expected as <c>0x25 0x02 0x26 0x0D</c> (Start + ACK + End + CR).
    /// </para>
    /// </remarks>
    public async Task<bool> ConnectHannesAsync(CancellationToken ct = default)
    {
        Debug.WriteLine($"[HannesHand] === ConnectHannesAsync ===");
        Debug.WriteLine($"[HannesHand] SelectedDevice: '{SelectedDevice}'");
        EnsureOpen();

        if (string.IsNullOrWhiteSpace(SelectedDevice))
        {
            Debug.WriteLine($"[HannesHand] FAILED: No device selected");
            return false;
        }

        var idx = _names.IndexOf(SelectedDevice!);
        if (idx < 0)
        {
            Debug.WriteLine($"[HannesHand] FAILED: Device not in scan list");
            return false;
        }

        var conId = _cons[idx];
        Debug.WriteLine($"[HannesHand] Connecting to device index {conId}...");

        try
        {
            _dongle.ClearInput();
            _dongle.WriteLine($"con {conId}");
            await Task.Delay(500, ct);

            _connected = true;

            Debug.WriteLine($"[HannesHand] Sending EnterCMDMode (ping)...");
            _dongle.ClearInput();
            WriteEmgemPacket((byte)Cmd.EnterCMDMode);

            await Task.Delay(1000, ct);

            var response = _dongle.ReadExisting();
            Debug.WriteLine($"[HannesHand] Response length: {response.Length}");
            Debug.WriteLine($"[HannesHand] Response (hex): {BitConverter.ToString(System.Text.Encoding.Latin1.GetBytes(response))}");

            var ackReply = $"{(char)Start}{(char)Cmd.ACK}{(char)End}\r";

            if (!response.Contains(ackReply))
            {
                Debug.WriteLine($"[HannesHand] No ACK received - ping failed");
                _connected = false;
                return false;
            }

            Debug.WriteLine($"[HannesHand] ACK received - ping successful!");
            _mode = StreamMode.Cmd;

            Debug.WriteLine($"[HannesHand] Configuring for ref control...");
            await ConfigureForRefControlAsync(ct);

            _streaming = true;
            _status = DeviceStatus.Ready;
            Debug.WriteLine($"[HannesHand] SUCCESS: Ready to receive control commands!");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HannesHand] EXCEPTION: {ex.GetType().Name} - {ex.Message}");
            _status = DeviceStatus.Error;
            _connected = false;
            return false;
        }
    }

    /// <summary>
    /// Disconnects from the Hannes prosthesis: switches to command mode, sends <c>dct</c>,
    /// and resets connection state.
    /// </summary>
    public void DisconnectHannes()
    {
        Debug.WriteLine($"[HannesHand] DisconnectHannes");
        if (!_dongle.IsOpen) return;

        try
        {
            if (_mode == StreamMode.Stream) SwitchMode();
            _streaming = false;

            _dongle.EnterDongleCommandMode();
            Thread.Sleep(200);
            _dongle.WriteLine("dct");
            Thread.Sleep(600);

            _connected = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HannesHand] DisconnectHannes exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses BLE scan output lines into the <see cref="_names"/>, <see cref="_cons"/>,
    /// <see cref="_rssis"/>, and <see cref="_addrs"/> lists.
    /// </summary>
    /// <param name="lines">Raw text lines from the dongle's scan response.</param>
    /// <remarks>
    /// Expected format: <c># {con} {rssi} {addr} {name}</c> (with or without a leading <c>#</c> token).
    /// Duplicate addresses are skipped.
    /// </remarks>
    private void ParseScan(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 5 || Array.IndexOf(p, "RSSI") >= 0) continue;

            try
            {
                int con, rssi;
                string addr, name;

                if (p[1].Contains("#"))
                {
                    con = int.Parse(p[2]);
                    rssi = int.Parse(p[3]);
                    addr = p[4];
                    name = p[5];
                }
                else
                {
                    con = int.Parse(p[1]);
                    rssi = int.Parse(p[2]);
                    addr = p[3];
                    name = p[4];
                }

                if (!_addrs.Contains(addr))
                {
                    _cons.Add(con);
                    _rssis.Add(rssi);
                    _addrs.Add(addr);
                    _names.Add(name);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[HannesHand] ParseScan error: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Throws if the serial port is not open.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the dongle port is closed.</exception>
    private void EnsureOpen()
    {
        if (!_dongle.IsOpen)
            throw new InvalidOperationException("Serial port not open. Call ConnectDongleAsync first.");
    }

    /// <summary>
    /// Builds and writes an EMGEM-framed packet: <c>0x25 | payload | 0x26 | CR | LF</c>.
    /// </summary>
    /// <param name="payload">One or more payload bytes to frame.</param>
    private void WriteEmgemPacket(params byte[] payload)
    {
        if (!_dongle.IsOpen)
        {
            Debug.WriteLine($"[HannesHand] WriteEmgemPacket: Port not open!");
            return;
        }

        var msg = new byte[payload.Length + 4];
        msg[0] = Start;                                          // 0x25 '%'
        System.Buffer.BlockCopy(payload, 0, msg, 1, payload.Length);
        msg[payload.Length + 1] = End;                            // 0x26 '&'
        msg[payload.Length + 2] = CR;                             // 0x0D '\r'
        msg[payload.Length + 3] = LF;                             // 0x0A '\n'

        Debug.WriteLine($"[HannesHand] WriteEmgemPacket: {BitConverter.ToString(msg)}");
        _dongle.WriteRaw(msg);
    }

    /// <summary>
    /// Toggles between command mode and stream mode by sending the appropriate EMGEM command.
    /// </summary>
    private void SwitchMode()
    {
        if (_mode == StreamMode.Stream)
        {
            WriteEmgemPacket((byte)Cmd.EnterCMDMode);
            _mode = StreamMode.Cmd;
        }
        else
        {
            WriteEmgemPacket((byte)Cmd.ExitCMDMode);
            _mode = StreamMode.Stream;
        }
    }

    /// <summary>
    /// Converts a <see cref="BitArray"/> to a single <see cref="byte"/> (first 8 bits).
    /// </summary>
    /// <param name="bits">Bit array to convert.</param>
    /// <returns>The byte representation of the first 8 bits.</returns>
    private static byte BitArrayToByte(BitArray bits)
    {
        var b = new byte[1];
        bits.CopyTo(b, 0);
        return b[0];
    }

    /// <summary>
    /// Configures the Hannes firmware for reference control: sets the active joint mask
    /// and control mode, then switches to stream mode.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The joint mask is built from the <see cref="Thumb"/>, <see cref="WristExFlex"/>,
    /// <see cref="WristSupPro"/>, and <see cref="HandOpenClose"/> toggle properties.
    /// Control mode is set to <see cref="ControlMode.UNITY_CONTROL"/>.
    /// </remarks>
    private async Task ConfigureForRefControlAsync(CancellationToken ct)
    {
        if (_mode == StreamMode.Stream) SwitchMode();

        var mask = new BitArray(8);
        mask[2] = Thumb;
        mask[5] = WristExFlex;
        mask[6] = WristSupPro;
        mask[7] = HandOpenClose;

        WriteEmgemPacket((byte)Cmd.RefControl, (byte)Ref.REF_JOINT_SET, BitArrayToByte(mask));
        WriteEmgemPacket((byte)Cmd.RefControl, (byte)Ref.REF_CONTROL_MODE, (byte)ControlMode.UNITY_CONTROL);

        SwitchMode();
        await Task.Delay(200, ct);
    }

    /// <summary>
    /// Sends a simultaneous 4-DOF reference command to the Hannes prosthesis.
    /// </summary>
    /// <param name="v">
    /// 4-element reference vector: [Hand (0–1), WristFE (0–1), WristPS (0–1), Thumb (0–1)].
    /// Values are clamped to [0, 1].
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Protocol mapping:</b>
    /// <list type="bullet">
    ///   <item><description>Hand: 0–1 → 0–100 (unsigned byte)</description></item>
    ///   <item><description>Wrist F/E: 0–1 → 0–100 (unsigned byte)</description></item>
    ///   <item><description>Wrist P/S: 0–1 → −100 to +100 (signed byte, negated: −(v−50)×2)</description></item>
    ///   <item><description>Thumb: 0–1 → −100 to +100 (signed byte, negated: −(v−50)×2)</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private void SendSimultaneousRefs(Vector<double> v)
    {
        if (v.Count < 4)
        {
            Debug.WriteLine($"[HannesHand] SendSimultaneousRefs: expected 4 values, got {v.Count}");
            return;
        }

        static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

        var hand = (byte)(Clamp01(v[0]) * 100.0);
        var wrFe = (byte)(Clamp01(v[1]) * 100.0);

        var wrPs0 = Clamp01(v[2]) * 100.0;
        var wrPsSigned = (sbyte)Math.Clamp(-(wrPs0 - 50.0) * 2.0, -100, 100);

        var thumb0 = Clamp01(v[3]) * 100.0;
        var thumbSigned = (sbyte)Math.Clamp(-(thumb0 - 50.0) * 2.0, -100, 100);

        var pkt = new byte[]
        {
            (byte)Cmd.SimultRefControl,
            0x00,                           // B2: must be 0x00
            hand,                           // B3: Hand ref (0–100)
            unchecked((byte)wrPsSigned),    // B4: Wrist P/S ref (−100 to +100, signed)
            wrFe,                           // B5: Wrist F/E ref (0–100)
            0x00,                           // B6: Elbow (not used)
            0x03,                           // B7: must be 0x03
            unchecked((byte)thumbSigned),   // B8: Thumb ref (−100 to +100, signed)
            0x00,                           // B9: 3D Digit (not used)
            0x00,                           // B10: Shoulder (not used)
            0x00                            // B11: must be 0x00
        };

        WriteEmgemPacket(pkt);
    }
}