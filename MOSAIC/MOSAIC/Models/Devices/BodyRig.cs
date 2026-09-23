using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.BodyRig;
using MOSAIC.Components.IO;
using MOSAIC.Diagnostics;
using MOSAIC.Models.FlowControl;
using MOSAIC.Visualization;
using Buffer = System.Buffer;

namespace MOSAIC.Models.Devices;

/// <summary>
/// Which source is currently driving a <see cref="BodyRig"/>.
/// </summary>
public enum BodyRigMode
{
    /// <summary>No input has arrived yet.</summary>
    Idle,

    /// <summary>Driven by a clock tick, reading binary frames from the serial port.</summary>
    Serial,

    /// <summary>Driven by an upstream block pushing a concatenated quaternion vector.</summary>
    Upstream
}

/// <summary>
/// A <see cref="BaseBlock"/> that receives orientation data from a DLR BodyRig device
/// (via an RN41 SPP Bluetooth serial dongle) or from upstream quaternion vectors,
/// computes forward kinematics over a configurable kinematic chain, and publishes
/// joint positions and orientations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Serial mode:</b> When the input block is a Timer, the BodyRig reads binary
/// frames from a serial port. Each frame consists of a start byte <c>0x55</c>,
/// <c>numSentValues × 4</c> data bytes, and an end byte <c>0xFF</c>.
/// </para>
/// <para>
/// <b>Quaternion mode:</b> When the input block pushes a <see cref="Vector{Double}"/>
/// of concatenated quaternions (w, x, y, z per sensor), the block bypasses serial I/O
/// and directly updates the kinematic chain.
/// </para>
/// <para>
/// The output is a <see cref="Vector{Double}"/> of length <c>numOfChannels × 7</c>,
/// containing <c>[W, X, Y, Z, posX, posY, posZ]</c> per segment. An additional UDP
/// stream sends the pose to Unity (localhost:3339).
/// </para>
/// </remarks>
/// <example>
/// <para>JSON pipeline configuration:</para>
/// <code language="json">
/// {
///   "Name": "BodyRig",
///   "Type": "BodyRig",
///   "Inputs": [ "Timer100Hz" ],
///   "Params": [ "COM5", "7", "C:\\Calibrations" ]
/// }
/// </code>
/// </example>
public sealed partial class BodyRig : BaseBlock
{

    #region Constants

    private const char FrameStart = (char)0x55;
    private const char FrameEnd   = (char)0xFF;
    private const int  StartEndSizeBytes = 1;
    private const int  NumSentValues     = 5;
    private const int  ValuesPerSegment  = 7;   // W, X, Y, Z, posX, posY, posZ
    private const int  UnityPort         = 3339;

    /// <summary>
    /// Windows-1252/Latin-1 is used purely as a byte-transparent byte-to-char transport for
    /// the binary serial frames: it round-trips all 256 byte values. It must be used on BOTH
    /// sides of the buffer - decoding with 1252 and re-encoding with <c>Encoding.Default</c>
    /// (UTF-8 on .NET Core) corrupts every byte >= 0x80, which is most of a float payload.
    /// </summary>
    private static readonly Encoding FrameTransport = Encoding.Latin1;

    #endregion

    #region Configuration

    /// <summary>Serial port name (e.g. "COM5").</summary>
    public string PortNumber { get; set; } = string.Empty;

    /// <summary>
    /// Number of kinematic chain segments (IMU channels). Assigning a new value
    /// rebuilds the kinematic chain and the output buffer, so the chain never
    /// drifts out of sync with the configured channel count.
    /// </summary>
    public int NumOfChannels
    {
        get => _numOfChannels;
        set
        {
            int clamped = Math.Max(1, value);
            if (clamped == _numOfChannels && _chain.Length == clamped) return;
            _numOfChannels = clamped;
            InitialiseChain();
        }
    }

    /// <summary>Number of detected devices (updated dynamically in quaternion mode).</summary>
    public int NumOfDevices { get; private set; } = 1;

    /// <summary>
    /// What each positional sensor slot physically is, when the pipeline makes it knowable.
    /// Empty until <see cref="DescribeSensorSources"/> is called.
    /// </summary>
    /// <remarks>
    /// Display only; the kinematics never consult it. The block cannot work this out for itself:
    /// in upstream mode it receives a concatenated vector, and which ESP node produced a given
    /// group is a fact about the graph rather than about the data.
    /// </remarks>
    public IReadOnlyList<BodyRigSensorSource> SensorSources { get; private set; } = [];

    /// <summary>Records what the pipeline resolved each sensor slot to, for the card to show.</summary>
    public void DescribeSensorSources(IReadOnlyList<BodyRigSensorSource>? sources) =>
        SensorSources = sources ?? [];

    /// <summary>
    /// The peripheral number feeding <paramref name="slot"/>, or <see langword="null"/> when the
    /// pipeline does not say.
    /// </summary>
    public int? PeripheralForSlot(int slot)
    {
        foreach (var source in SensorSources)
            if (source.Slot == slot) return source.PeripheralId;

        return null;
    }

    /// <summary>Number of segments currently allocated in the kinematic chain.</summary>
    public int SegmentCount => _chain.Length;

    /// <summary>
    /// Which source last drove this block. <see cref="OnReceive"/> discriminates on the sender
    /// type, and that is the only honest source for it — the block's declared inputs are names,
    /// not types, so the mode cannot be inferred from configuration.
    /// </summary>
    public BodyRigMode Mode { get; private set; } = BodyRigMode.Idle;

    /// <summary>
    /// Whether the serial port is actually open, read from the port itself.
    /// </summary>
    /// <remarks>
    /// <see cref="Connect"/> records a failure in <see cref="Info"/> and returns normally rather
    /// than throwing, so a caller that merely watches for an exception can believe it is connected
    /// over a port that never opened. This is the truth.
    /// </remarks>
    public bool IsPortOpen => _serialPort is { IsOpen: true };

    #endregion

    #region Internal State

    // Per-sensor arrival counters. Incremented in ApplySensorQuaternion, which is the single
    // funnel BOTH the serial and the upstream path pass through, so one counter gives truthful
    // liveness in either mode. Written from the serial stream thread, so accessed with
    // Interlocked/Volatile. Liveness cannot be derived any other way: NumOfDevices is only ever
    // assigned on the upstream path, and a motionless-but-healthy IMU defeats any attempt to
    // infer activity from a changing orientation.
    private const int MaxTrackedSensors = 64;
    private readonly int[] _sensorPackets = new int[MaxTrackedSensors];

    private int             _numOfChannels = 10;
    private BodySegment[]   _chain  = Array.Empty<BodySegment>();
    private double[]        _sample = Array.Empty<double>();
    private int             _bytesPerFrame;

    // Serial I/O
    private SerialPort?    _serialPort;
    private StreamManager? _streamManager;

    // Guards _bufferParser. A dedicated object is required: _bufferParser is a string
    // that gets reassigned on every append, so locking on it would change the monitor
    // out from under waiting threads and provide no mutual exclusion at all.
    private readonly object _bufferLock = new();
    private string          _bufferParser = string.Empty;

    // UDP to Unity
    private readonly UdpClient _unityClient = new();

    // Calibration
    private string?  _calibrationDirectory;
    private string[] _profiles = Array.Empty<string>();
    private string?  _calibrationFile;
    private int      _currentProfile;

    #endregion

    #region Observable Properties

    /// <summary>Observable status information for UI binding.</summary>
    public BodyRigInfo Info { get; } = new();

    /// <summary>
    /// Live plot of the published pose, shown on the device card. Every other block that
    /// emits a stream owns one of these; without it the block computes correctly but nothing
    /// it produces is ever drawn, which reads to the user as "no data".
    /// </summary>
    public BlockVisualization? Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    #endregion

    #region Construction / Factory

    /// <summary>
    /// Initialises a new <see cref="BodyRig"/> instance.
    /// </summary>
    /// <param name="name">Display name for this block.</param>
    /// <param name="desiredRate">Desired processing rate in Hz.</param>
    public BodyRig(string name = "BodyRig", double desiredRate = 100)
    {
        Name = name;
        DesiredRate = desiredRate;
        _unityClient.Connect("localhost", UnityPort);
        InitialiseChain();
    }

    /// <summary>
    /// Factory method that creates a <see cref="BodyRig"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">The application's dependency injection service provider.</param>
    /// <param name="m">
    /// JSON model. <c>Params</c> format:
    /// <c>[portName, numChannels, calibrationDirectory, profileFileName]</c>.
    /// </param>
    /// <returns>A configured <see cref="BodyRig"/> block, with its calibration applied.</returns>
    public static BodyRig ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "BodyRig";
        var rate = m.DesiredRate ?? 100;

        var block = ActivatorUtilities.CreateInstance<BodyRig>(sp, name, rate);

        // Params are positional and all optional: [portName, numChannels, calibrationDirectory].
        var ps = m.Params;
        if (ps is { Count: > 0 })
            block.PortNumber = ps[0]?.ToString()?.Trim() ?? string.Empty;

        if (ps is { Count: > 1 } && int.TryParse(ps[1]?.ToString()?.Trim(), out int ch))
            block.NumOfChannels = ch;

        if (ps is { Count: > 2 })
        {
            var dir = ps[2]?.ToString()?.Trim();
            block._calibrationDirectory = string.IsNullOrEmpty(dir) ? null : dir;
        }

        // NumOfChannels rebuilds the chain on assignment, but a config that omits it
        // (or repeats the default) must still end up with a chain and a profile list.
        block.InitialiseChain();

        // Fourth param names the profile that was current when the pipeline was saved. Without
        // it the directory's first file wins, which is only right by accident.
        if (ps is { Count: > 3 })
            block.SelectProfileByName(ps[3]?.ToString()?.Trim() ?? string.Empty);

        // Apply it. Until this call a reopened pipeline came back on constructor defaults -
        // 1 m links, no parents - and that was invisible on the card, because a fresh chain
        // pre-binds every segment to its own sensor and so already reads as configured.
        // No-ops when the directory holds nothing.
        block.LoadCalibration();

        return block;
    }

    #endregion

    #region Chain Initialisation

    /// <summary>
    /// (Re)creates the kinematic chain array and output sample buffer, and refreshes
    /// the calibration profile list. Safe to call at any time; any existing chain
    /// topology and calibration are discarded.
    /// </summary>
    public void InitialiseChain()
    {
        _chain = new BodySegment[_numOfChannels];
        for (int i = 0; i < _chain.Length; i++)
        {
            _chain[i] = new BodySegment { SensorIndex = i, OrdinalIndex = i };
        }

        _sample = new double[_numOfChannels * ValuesPerSegment];

        // Rescan only when a directory is configured. An explicit path handed to
        // LoadCalibration(path) has to survive a chain rebuild, and an unconditional
        // rescan would clear it.
        if (_calibrationDirectory is not null) RescanProfiles();
    }

    #endregion

    #region Serial Connection

    /// <summary>
    /// Opens the serial port and starts streaming binary frames from the BodyRig dongle.
    /// </summary>
    public void Connect()
    {
        if (string.IsNullOrWhiteSpace(PortNumber))
        {
            Info.Flag = "No port configured";
            return;
        }

        _bytesPerFrame = StartEndSizeBytes + 4 * NumSentValues + StartEndSizeBytes;

        try
        {
            _serialPort = new SerialPort(PortNumber, 115200, Parity.None, 8, StopBits.One)
            {
                Encoding = FrameTransport,
                ReceivedBytesThreshold = _bytesPerFrame,
                ReadBufferSize = 100 * _bytesPerFrame,
                ReadTimeout = 10
            };
            _serialPort.Open();
        }
        catch (Exception ex)
        {
            Info.Flag = $"Connection failed: {ex.Message}";
            Console.WriteLine($"[BodyRig] {Info.Flag}");
            return;
        }

        Info.Flag = "Streaming";
        Console.WriteLine($"[BodyRig] Connected on {PortNumber}");
        StartStream();
    }

    /// <summary>
    /// Stops streaming and closes the serial port.
    /// </summary>
    public void Disconnect()
    {
        StopStream();
        _serialPort?.Close();
        _serialPort?.Dispose();
        _serialPort = null;
        Info.Flag = "Disconnected";
        Console.WriteLine("[BodyRig] Disconnected.");
    }

    private void StartStream()
    {
        if (_serialPort is not { IsOpen: true }) return;

        _streamManager = new StreamManager(_serialPort.BaseStream, _bytesPerFrame, 0);
        _streamManager.DataReceived += OnSerialDataReceived!;
        _streamManager.StartStreaming();
    }

    private void StopStream()
    {
        if (_streamManager is null) return;
        _streamManager.StopStreaming();
        _streamManager.DataReceived -= OnSerialDataReceived!;
        _streamManager = null;
    }

    private void OnSerialDataReceived(byte[] buffer, int count)
    {
        lock (_bufferLock)
        {
            string addition = FrameTransport.GetString(buffer, 0, count);

            if (_bufferParser.Length <= _bytesPerFrame * 10)
            {
                _bufferParser += addition;
                Info.Flag = "Streaming";
            }
            else
            {
                // Overflow — discard oldest data
                Info.Flag = "Overflow";
                Info.FrameErrors++;
                _bufferParser = addition.Length >= _bytesPerFrame * 10
                    ? (_serialPort?.ReadExisting() ?? string.Empty)
                    : _bufferParser + addition;
            }
        }
    }

    #endregion

    #region Data Processing

    /// <summary>
    /// Processes incoming pipeline data. Handles both serial timer ticks
    /// and upstream quaternion vectors.
    /// </summary>
    protected override void OnReceive(object sender, object value)
    {
        if (sender is ClockBlock)
        {
            Mode = BodyRigMode.Serial;
            ProcessSerialFrame();
        }
        else
        {
            Mode = BodyRigMode.Upstream;
            ProcessQuaternionVector(value as Vector<double>);
        }

        PublishOutput();
    }

    /// <summary>
    /// Parses a single binary frame from the serial buffer and updates the kinematic chain.
    /// </summary>
    private void ProcessSerialFrame()
    {
        if (_bytesPerFrame <= 0) return;

        string localBuffer;
        lock (_bufferLock) localBuffer = _bufferParser;

        if (localBuffer.Length < _bytesPerFrame) return;

        int startIndex = localBuffer.IndexOf(FrameStart);
        if (startIndex < 0)
        {
            // Nothing buffered can start a frame, so drop it all: otherwise a burst of
            // noise sits at the head of the buffer forever and wedges the parser.
            Info.Flag = "Frame not found";
            Info.FrameErrors++;
            ConsumeBuffer(localBuffer.Length);
            return;
        }

        // Marker found but the frame is not fully buffered yet: realign and wait.
        if (startIndex + _bytesPerFrame > localBuffer.Length)
        {
            if (startIndex > 0) ConsumeBuffer(startIndex);
            return;
        }

        string frame = localBuffer.Substring(startIndex, _bytesPerFrame);

        // Consume it either way - a malformed frame must not be retried forever.
        ConsumeBuffer(startIndex + _bytesPerFrame);

        // Validate frame end marker
        if (frame[^1] != FrameEnd)
        {
            Info.Flag = "Frame ending error";
            Info.FrameErrors++;
            return;
        }

        // Decode float values from frame bytes
        byte[] frameBytes = FrameTransport.GetBytes(frame);
        var rawData = new float[NumSentValues];

        for (int ch = 0; ch < NumSentValues; ch++)
        {
            int offset = ch * 4 + StartEndSizeBytes;
            rawData[ch] = BitConverter.ToSingle(frameBytes, offset);
        }

        // Determine which segment this frame belongs to and update its orientation
        int sensorId = BitConverter.GetBytes(rawData[0])[3] - 0x31;
        ApplySensorQuaternion(sensorId, rawData[2], rawData[3], rawData[4], rawData[1]);

        UpdateSampleBuffer();
    }

    /// <summary>Drops the first <paramref name="count"/> characters from the parse buffer.</summary>
    private void ConsumeBuffer(int count)
    {
        lock (_bufferLock)
        {
            _bufferParser = count >= _bufferParser.Length
                ? string.Empty
                : _bufferParser.Remove(0, count);
        }
    }

    /// <summary>
    /// Applies one sensor reading to every chain segment bound to <paramref name="sensorId"/>.
    /// </summary>
    private void ApplySensorQuaternion(int sensorId, float x, float y, float z, float w)
    {
        // Counted before the match loop on purpose: a sensor that arrives but is bound to no
        // segment still counts as arriving, which is exactly the case a user needs to see.
        if ((uint)sensorId < MaxTrackedSensors)
            Interlocked.Increment(ref _sensorPackets[sensorId]);

        for (int i = 0; i < _chain.Length; i++)
        {
            if (_chain[i].SensorIndex == sensorId)
                _chain[i].UpdateOrientation(x, y, z, w);
        }
    }

    /// <summary>
    /// Processes a concatenated quaternion vector from an upstream block.
    /// </summary>
    private void ProcessQuaternionVector(Vector<double>? quaternions)
    {
        if (quaternions is null) return;

        // Each sensor contributes 4 values: w, x, y, z. A trailing partial group is
        // ignored rather than indexed past the end of the vector.
        int complete = quaternions.Count / 4;

        for (int sensorId = 0; sensorId < complete; sensorId++)
        {
            int i = sensorId * 4;
            ApplySensorQuaternion(sensorId,
                (float)quaternions[i + 1],   // x
                (float)quaternions[i + 2],   // y
                (float)quaternions[i + 3],   // z
                (float)quaternions[i]);      // w
        }

        NumOfDevices = complete;
        UpdateSampleBuffer();
    }

    /// <summary>
    /// Copies current chain state (orientations + positions) into the flat output array.
    /// Skips segments with NaN or Infinity values.
    /// </summary>
    private void UpdateSampleBuffer()
    {
        for (int i = 0; i < _chain.Length; i++)
        {
            var seg = _chain[i];
            var q = seg.Orientation;
            var p = seg.Position;

            // Guard against invalid sensor data. Both NaN and infinity have to be
            // rejected on the quaternion as well as the position - a NaN quaternion
            // otherwise propagates straight through the forward kinematics.
            if (!float.IsFinite(q.W) || !float.IsFinite(q.X) ||
                !float.IsFinite(q.Y) || !float.IsFinite(q.Z) ||
                !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
            {
                continue; // Keep last known good values
            }

            int offset = ValuesPerSegment * i;
            _sample[offset + 0] = q.W;
            _sample[offset + 1] = q.X;
            _sample[offset + 2] = q.Y;
            _sample[offset + 3] = q.Z;
            _sample[offset + 4] = p.X;
            _sample[offset + 5] = p.Y;
            _sample[offset + 6] = p.Z;
        }
    }

    /// <summary>
    /// Publishes the current sample as a MathNet vector and sends the body pose via UDP to Unity.
    /// </summary>
    private void PublishOutput()
    {
        var result = Vector<double>.Build.DenseOfArray(_sample);
        Publish(result);
        Viz?.Feed(result);
        SendBodyPoseToUnity();

        Info.FramesProcessed++;
    }

    #endregion

    #region Unity UDP Output

    /// <summary>
    /// Sends per-segment pose data (ID + quaternion + position) as float arrays over UDP.
    /// </summary>
    private void SendBodyPoseToUnity()
    {
        if (_disposed) return;

        var buffer = new float[8];
        var id = new byte[4];

        for (int i = 0; i < _chain.Length; i++)
        {
            // Segment id as 4 ASCII digits, e.g. segment 0 -> "0001", segment 11 -> "0012".
            // The old code only bumped the last digit, so ids collided/garbled past segment 9.
            WriteAsciiId(id, i + 1);
            buffer[0] = BitConverter.ToSingle(id, 0);

            int offset = i * ValuesPerSegment;
            buffer[1] = (float)_sample[offset + 0]; // W
            buffer[2] = (float)_sample[offset + 1]; // X
            buffer[3] = (float)_sample[offset + 2]; // Y
            buffer[4] = (float)_sample[offset + 3]; // Z
            buffer[5] = (float)_sample[offset + 4]; // posX
            buffer[6] = (float)_sample[offset + 5]; // posY
            buffer[7] = (float)_sample[offset + 6]; // posZ

            var byteArray = new byte[buffer.Length * 4];
            Buffer.BlockCopy(buffer, 0, byteArray, 0, byteArray.Length);

            try { _unityClient.Send(byteArray, byteArray.Length); }
            catch { /* Unity not available — silently ignore */ }
        }
    }

    /// <summary>Writes <paramref name="value"/> as four zero-padded ASCII digits into <paramref name="id"/>.</summary>
    private static void WriteAsciiId(byte[] id, int value)
    {
        int v = ((value % 10000) + 10000) % 10000;
        id[0] = (byte)('0' + v / 1000);
        id[1] = (byte)('0' + v / 100 % 10);
        id[2] = (byte)('0' + v / 10 % 10);
        id[3] = (byte)('0' + v % 10);
    }

    #endregion

    #region Kinematic Chain Manipulation

    /// <summary>
    /// Computes the relative orientation of segment <paramref name="index1"/>
    /// with respect to segment <paramref name="index2"/>.
    /// </summary>
    public QuaternionF OrientationRelativeTo(int index1, int index2)
    {
        if (index1 < 0 || index1 >= _chain.Length ||
            index2 < 0 || index2 >= _chain.Length)
            return QuaternionF.Identity;

        return _chain[index2].Orientation.Conjugate() * _chain[index1].Orientation;
    }

    /// <summary>Sets the link length vector on a kinematic chain segment.</summary>
    public void SetLinkLength(int index, float lx, float ly, float lz)
    {
        if (index >= 0 && index < _chain.Length)
            _chain[index].LinkLength = new Vector3F(lx, ly, lz);
    }

    /// <summary>
    /// Sets the DH-to-sensor mapping from Euler angles (degrees) on a segment, in the
    /// <c>(roll, pitch, yaw)</c> order returned by <see cref="GetDhAngles"/>.
    /// </summary>
    public void SetDhAngles(int index, float roll, float pitch, float yaw)
    {
        if (index >= 0 && index < _chain.Length)
            _chain[index].SetDh(roll, pitch, yaw);
    }

    /// <summary>Sets the DH-to-sensor mapping from quaternion components on a segment.</summary>
    public void SetDhQuaternion(int index, float w, float x, float y, float z)
    {
        if (index >= 0 && index < _chain.Length)
            _chain[index].SetDh(w, x, y, z);
    }

    /// <summary>
    /// Sets the parent of a segment in the kinematic chain, or detaches it when
    /// <paramref name="parentIndex"/> is -1. Cycles and self-parenting are rejected.
    /// </summary>
    public void SetParent(int childIndex, int parentIndex)
    {
        if (childIndex < 0 || childIndex >= _chain.Length) return;

        var child = _chain[childIndex];

        if (parentIndex < 0)
        {
            DetachFromParent(child);
            child.Position = Vector3F.Zero;
            return;
        }

        if (parentIndex >= _chain.Length) return;
        if (childIndex == parentIndex) return;

        var parent = _chain[parentIndex];
        if (ReferenceEquals(child.Parent, parent)) return;
        if (parent.IsDescendantOf(child)) return;

        // Unhook from the old parent first, or it keeps pointing at a child it no longer owns
        // and the recursive forward-kinematics walk reaches that segment twice.
        DetachFromParent(child);

        // The new parent keeps every child it already has. Evicting one to make room is what
        // silently flattened branching models — a torso with two arms came back as a torso with
        // one, and a second arm orphaned at the world origin.
        child.Parent = parent;
        parent.AddChild(child);
    }

    /// <summary>Removes the two-way link between <paramref name="segment"/> and its parent.</summary>
    private static void DetachFromParent(BodySegment segment)
    {
        segment.Parent?.RemoveChild(segment);
        segment.Parent = null;
    }

    /// <summary>Sets the sensor index on a kinematic chain segment.</summary>
    public void SetSensorIndex(int segmentIndex, int sensorId)
    {
        if (segmentIndex >= 0 && segmentIndex < _chain.Length)
            _chain[segmentIndex].SensorIndex = sensorId;
    }

    /// <summary>Gets the link lengths for a segment.</summary>
    public Vector3F GetLinkLengths(int index) =>
        index >= 0 && index < _chain.Length ? _chain[index].LinkLength : Vector3F.Zero;

    /// <summary>
    /// Gets the DH Euler angles for a segment as <c>(roll, pitch, yaw)</c> in <b>radians</b>.
    /// </summary>
    public Vector3F GetDhAngles(int index) =>
        index >= 0 && index < _chain.Length
            ? _chain[index].DhRelativeToGyro.ToEulerAngles()
            : Vector3F.Zero;

    /// <summary>Gets the parent index for a segment (-1 if root).</summary>
    public int GetParentIndex(int index)
    {
        if (index < 0 || index >= _chain.Length) return -1;
        var parent = _chain[index].Parent;
        return parent is not null ? Array.IndexOf(_chain, parent) : -1;
    }

    /// <summary>Gets the sensor index assigned to a segment.</summary>
    public int GetSensorIndex(int index) =>
        index >= 0 && index < _chain.Length ? _chain[index].SensorIndex : -1;

    /// <summary>
    /// Gets the chain segment at <paramref name="index"/>, or <see langword="null"/>
    /// when the index is outside the chain.
    /// </summary>
    public BodySegment? GetSegment(int index) =>
        index >= 0 && index < _chain.Length ? _chain[index] : null;

    #endregion

    #region Calibration

    /// <summary>
    /// Loads calibration data from <paramref name="path"/>, making it the current profile.
    /// </summary>
    /// <remarks>
    /// The parameterless overload reads whatever the block already considers current, which is
    /// why a path typed into the card used to be ignored.
    /// </remarks>
    public void LoadCalibration(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _calibrationFile = path;
        LoadCalibration();
    }

    /// <summary>
    /// Loads calibration data from the current calibration file.
    /// </summary>
    public void LoadCalibration()
    {
        if (_calibrationFile is null) return;

        try
        {
            var lines = File.ReadLines(_calibrationFile).ToArray();
            int count = Math.Min(_chain.Length, lines.Length);

            // Two passes: every segment's own values first, then the parent links, so a
            // file that references a later segment as a parent still wires up correctly.
            var parents = new int[count];

            for (int i = 0; i < count; i++)
            {
                parents[i] = -1;

                string[] par = lines[i].Split(':');
                if (par.Length < 9) continue;

                SetLinkLength(i,
                    Convert.ToSingle(par[0], CultureInfo.InvariantCulture),
                    Convert.ToSingle(par[1], CultureInfo.InvariantCulture),
                    Convert.ToSingle(par[2], CultureInfo.InvariantCulture));

                SetDhQuaternion(i,
                    Convert.ToSingle(par[3], CultureInfo.InvariantCulture),
                    Convert.ToSingle(par[4], CultureInfo.InvariantCulture),
                    Convert.ToSingle(par[5], CultureInfo.InvariantCulture),
                    Convert.ToSingle(par[6], CultureInfo.InvariantCulture));

                parents[i] = Convert.ToInt32(par[7], CultureInfo.InvariantCulture);
                _chain[i].SensorIndex = Convert.ToInt32(par[8], CultureInfo.InvariantCulture);

                // Field ten onwards is the segment name, rejoined because a name is allowed to
                // contain the separator. Files written before names were persisted stop at nine,
                // which is why the length guard above still demands only nine. The file is
                // authoritative either way: a record with no name clears the one in memory, so
                // loading profile B cannot leave profile A's labels behind.
                var name = par.Length > 9
                    ? string.Join(":", par, 9, par.Length - 9).Trim()
                    : string.Empty;
                _chain[i].Name = name.Length > 0 ? name : null;
            }

            // SetParent maintains both link directions and rejects cycles; assigning
            // Parent directly (as this used to) left every Child pointer null, so the
            // chain never propagated an update past its root.
            for (int i = 0; i < count; i++)
                SetParent(i, parents[i] < count ? parents[i] : -1);

            Console.WriteLine($"[BodyRig] Calibration loaded: {_calibrationFile}");
        }
        catch (Exception ex)
        {
            Log.Error("BodyRig", Name, ex, "Calibration load failed.");
        }
    }

    /// <summary>
    /// Stores the current chain to <paramref name="filename"/> and makes it the current profile.
    /// </summary>
    /// <returns><see langword="true"/> when the file was actually written.</returns>
    /// <remarks>
    /// Failures are still swallowed, because a failed store must not take down a running
    /// pipeline - but they are now reported. A caller that announces "stored" without checking
    /// this repeats the defect that made the first profile in a fresh directory impossible to
    /// create from inside the app while the card claimed success.
    /// </remarks>
    public bool StoreCalibration(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            Console.WriteLine("[BodyRig] Calibration store failed: no filename given");
            return false;
        }

        if (Directory.Exists(filename))
        {
            Console.WriteLine($"[BodyRig] Calibration store failed: {filename} is a directory");
            return false;
        }

        try
        {
            // Typing a folder that does not exist yet and pressing Store is the normal way the
            // very first profile gets created; refusing it would leave the card unusable from
            // scratch, which is the hole this fix closes.
            var parent = Path.GetDirectoryName(filename);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            // Scoped so the handle is closed before RescanProfiles enumerates the directory.
            using (var writer = new StreamWriter(filename))
            {
                for (int i = 0; i < _chain.Length; i++)
                {
                    var link = _chain[i].LinkLength;
                    var dh = _chain[i].DhRelativeToGyro;
                    int parentIdx = _chain[i].Parent is not null
                        ? Array.IndexOf(_chain, _chain[i].Parent)
                        : -1;

                    // Invariant culture on both sides: a decimal-comma locale would otherwise
                    // write "0,5" into a colon-separated file that LoadCalibration cannot read.
                    writer.WriteLine(string.Join(":", new[]
                    {
                        link.X.ToString(CultureInfo.InvariantCulture),
                        link.Y.ToString(CultureInfo.InvariantCulture),
                        link.Z.ToString(CultureInfo.InvariantCulture),
                        dh.W.ToString(CultureInfo.InvariantCulture),
                        dh.X.ToString(CultureInfo.InvariantCulture),
                        dh.Y.ToString(CultureInfo.InvariantCulture),
                        dh.Z.ToString(CultureInfo.InvariantCulture),
                        parentIdx.ToString(CultureInfo.InvariantCulture),
                        _chain[i].SensorIndex.ToString(CultureInfo.InvariantCulture),

                        // Tenth field, and last on purpose: the loader rejoins everything from
                        // here on, so a name containing the separator needs no escaping. Only
                        // line breaks have to go, since those would split the record in two.
                        SanitiseName(_chain[i].Name)
                    }));
                }
            }

            // The file just written is the one being worked on. Leaving the selection wherever
            // it happened to be means the next Load quietly reads a different profile back.
            RescanProfiles();
            SelectProfile(filename);

            Console.WriteLine($"[BodyRig] Calibration stored: {filename}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("BodyRig", Name, ex, "Calibration store failed.");
            return false;
        }
    }

    /// <summary>Removes the characters that would break the one-record-per-line format.</summary>
    private static string SanitiseName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : name.Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>
    /// Makes <paramref name="path"/> the current profile, and points <see cref="NextProfile"/>
    /// and <see cref="PreviousProfile"/> at its position rather than the start of the list.
    /// </summary>
    private void SelectProfile(string path)
    {
        _calibrationFile = path;
        int index = Array.FindIndex(_profiles,
            p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _currentProfile = index >= 0 ? index : 0;
    }

    /// <summary>
    /// Makes the profile whose <em>file name</em> matches <paramref name="fileName"/> current,
    /// if the calibration directory holds one; leaves the selection alone when it does not.
    /// </summary>
    /// <remarks>
    /// Matching on the bare name rather than a full path is what lets a saved pipeline record
    /// which profile it was using without hard-coding one machine's folder layout a second time.
    /// </remarks>
    public void SelectProfileByName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;

        int index = Array.FindIndex(_profiles,
            p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;

        _currentProfile = index;
        _calibrationFile = _profiles[index];
    }

    /// <summary>Advances to the next calibration profile and loads it.</summary>
    public void NextProfile()
    {
        if (_profiles.Length == 0) return;
        _currentProfile = (_currentProfile + 1) % _profiles.Length;
        _calibrationFile = _profiles[_currentProfile];
        LoadCalibration();
    }

    /// <summary>Returns to the previous calibration profile and loads it.</summary>
    public void PreviousProfile()
    {
        if (_profiles.Length == 0) return;
        _currentProfile = (_currentProfile - 1 + _profiles.Length) % _profiles.Length;
        _calibrationFile = _profiles[_currentProfile];
        LoadCalibration();
    }

    /// <summary>Gets the current calibration file path.</summary>
    public string? CalibrationName => _calibrationFile;

    /// <summary>Gets the calibration profile paths discovered in the calibration directory.</summary>
    public IReadOnlyList<string> Profiles => _profiles;

    /// <summary>
    /// Total packets seen for <paramref name="sensorId"/> since the block was created, counted
    /// whether or not any segment is bound to it. Poll it and watch for an increase to determine
    /// liveness; the absolute value is only meaningful as a difference.
    /// </summary>
    public int GetSensorPacketCount(int sensorId) =>
        (uint)sensorId < MaxTrackedSensors ? Volatile.Read(ref _sensorPackets[sensorId]) : 0;

    /// <summary>The calibration directory scanned for profiles. Assigning re-scans it.</summary>
    public string? CalibrationDirectory
    {
        get => _calibrationDirectory;
        set
        {
            _calibrationDirectory = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            RescanProfiles();
        }
    }

    /// <summary>Re-reads the calibration directory and refreshes <see cref="Profiles"/>.</summary>
    public void RescanProfiles()
    {
        if (_calibrationDirectory is null)
        {
            _profiles = Array.Empty<string>();
            _calibrationFile = null;
            _currentProfile = 0;
            return;
        }

        try
        {
            _profiles = Directory.GetFiles(_calibrationDirectory);
            _currentProfile = 0;

            // Null, never the directory itself. Pointing the current *file* at a *directory*
            // made StoreCalibration open a StreamWriter on it, which throws
            // UnauthorizedAccessException straight into the swallowing catch - so the first
            // profile in a fresh directory could never be created from inside the app, while
            // the card still reported that it had been stored.
            _calibrationFile = _profiles.Length > 0 ? _profiles[0] : null;
        }
        catch
        {
            _profiles = Array.Empty<string>();
            _calibrationFile = null;
        }
    }

    #endregion

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "BodyRig";

    /// <summary>
    /// Emits the positional params
    /// <c>[port, channels, calibrationDirectory, profileFileName]</c> that
    /// <see cref="ConfigureInput"/> reads back.
    /// </summary>
    /// <remarks>
    /// Without this the base implementation returns <see langword="null"/> and a saved pipeline
    /// records <c>Params: null</c>, silently discarding the port, the channel count and the
    /// calibration directory. The profile name is the fourth field because the directory alone
    /// only says where the profiles live, not which one the rig was configured with.
    /// </remarks>
    protected override IReadOnlyList<object>? GetJsonParams() =>
        new object[]
        {
            PortNumber,
            NumOfChannels,
            CalibrationDirectory ?? string.Empty,

            // Bare file name, resolved against the directory above on the way back in, so a
            // pipeline stays portable between machines that keep their calibrations elsewhere.
            _calibrationFile is not null ? Path.GetFileName(_calibrationFile) : string.Empty
        };

    #endregion

    #region Dispose

    private bool _disposed;

    /// <summary>Disconnects serial, closes the Unity UDP client, and releases all resources.</summary>
    public override void Dispose()
    {
        if (_disposed)
        {
            base.Dispose();
            return;
        }

        _disposed = true;
        Disconnect();
        _unityClient.Dispose();
        base.Dispose();
    }

    #endregion
}

/// <summary>
/// Observable status model for the <see cref="BodyRig"/> block.
/// </summary>
public partial class BodyRigInfo : ObservableObject
{
    /// <summary>Current status flag (e.g. "Streaming", "Overflow", "Disconnected").</summary>
    [ObservableProperty] private string _flag = "Ready";

    /// <summary>Running count of frames processed.</summary>
    [ObservableProperty] private int _framesProcessed;

    /// <summary>
    /// Running count of parser faults (overflow, missing frame start, bad frame end).
    /// </summary>
    /// <remarks>
    /// <see cref="Flag"/> is overwritten with "Streaming" on every received chunk, so an error
    /// state survives roughly one frame and a UI poll almost never observes it. This counter is
    /// monotonic, so a poll can compare it against its previous reading.
    /// </remarks>
    [ObservableProperty] private int _frameErrors;
}