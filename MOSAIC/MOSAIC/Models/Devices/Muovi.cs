using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Devices.Muovi;
using MOSAIC.Diagnostics;
using MOSAIC.Visualization.ScopeMonitor;
using static MOSAIC.Components.Devices.Muovi.MuoviConstants;


namespace MOSAIC.Models.Devices;

/// <summary>
/// A <see cref="BaseBlock"/> that streams high-density surface EMG (HD-sEMG), EEG, or IMU data
/// from one or more Muovi probes connected through an OT Bioelettronica SyncStation.
/// </summary>
/// <remarks>
/// <para>
/// The Muovi probe is a wireless, wearable, high-density biosignal acquisition device
/// manufactured by <strong>OT Bioelettronica</strong> (Turin, Italy). Each probe features
/// a 32-electrode grid and a 9-axis IMU. Up to four probes can be synchronised through
/// a single SyncStation base unit, which exposes a TCP server at <c>192.168.76.1:54320</c>.
/// </para>
/// <para>
/// Firmware documentation and protocol specifications are available from the manufacturer at
/// <see href="https://otbioelettronica.it/en/download/#55-171-wpfd-muovi"/>.
/// </para>
/// <para><strong>Supported signal modes:</strong></para>
/// <list type="table">
///   <listheader>
///     <term>Mode</term>
///     <description>Sample rate / gain</description>
///   </listheader>
///   <item>
///     <term><see cref="MuoviSignalMode.Emg"/></term>
///     <description>2 000 Hz, gain ×8 (286.1 nV/bit, ±9.375 mV range)</description>
///   </item>
///   <item>
///     <term><see cref="MuoviSignalMode.EmgLowGain"/></term>
///     <description>2 000 Hz, gain ×4 (572.2 nV/bit, ±18.75 mV range)</description>
///   </item>
///   <item>
///     <term><see cref="MuoviSignalMode.Eeg"/></term>
///     <description>500 Hz, EEG-optimised front-end</description>
///   </item>
/// </list>
/// <para><strong>Output modes:</strong></para>
/// <list type="bullet">
///   <item>
///     <description>
///     <strong>Matrix push</strong> — when <see cref="BaseBlock.DesiredRate"/> is lower than
///     the nominal sample rate, the reader thread accumulates
///     <c>NominalRate / DesiredRate</c> samples and publishes a
///     <see cref="Matrix{T}"/> directly; no upstream timer is required.
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>Vector timer</strong> — when <see cref="BaseBlock.DesiredRate"/> equals the
///     nominal rate, the reader decodes every sample and a timer tick publishes the latest
///     <see cref="Vector{T}"/>.
///     </description>
///   </item>
/// </list>
/// <para><strong>Protocol overview (SyncStation v2.8):</strong></para>
/// <para>
/// The configuration packet consists of a START byte encoding the number of CONTROL bytes
/// that follow (one per enabled probe), plus a CRC-8 trailer. The SyncStation allocates
/// frame bandwidth only for probes whose CONTROL bytes are present, so the total raw columns
/// per sample equals <c>enabledProbes × 38 + 6</c> (38 channels per probe plus 6 SyncStation
/// accessory channels). Each TCP burst contains exactly 18 consecutive samples.
/// </para>
/// </remarks>
/// <example>
/// <para>Minimal JSON configuration streaming two probes in EMG mode with IMU:</para>
/// <code language="json">
/// {
///   "Name": "HD-EMG",
///   "Type": "Muovi",
///   "DesiredRate": 100,
///   "Params": [ "1", "3", "emg" ]
/// }
/// </code>
/// <para>
/// <c>Params</c> accepts zero-based probe indices and optional keywords:
/// <c>"emg"</c>, <c>"emg_lowgain"</c> / <c>"lowgain"</c>, <c>"eeg"</c>, and <c>"imu"</c>.
/// If no indices are specified, probe 0 is used by default.
/// </para>
/// </example>
/// <seealso cref="MuoviSingleProbe"/>
/// <seealso cref="MuoviConstants"/>
/// <seealso cref="Components.Devices.Muovi.MuoviStreamInfo"/>
public sealed partial class Muovi : BaseBlock
{
    #region Constants

    /// <summary>Default IP address of the SyncStation Wi-Fi access point.</summary>
    private const string SyncStationIp = "192.168.76.1";

    /// <summary>TCP port exposed by the SyncStation firmware.</summary>
    private const int SyncStationPort = 54320;

    /// <summary>Number of accessory channels appended by the SyncStation after all probe data.</summary>
    private const int SyncStationExtraCh = 6;

    /// <summary>
    /// Number of samples the SyncStation packs into each TCP burst.
    /// This value is fixed in firmware and cannot be changed.
    /// </summary>
    private const int SubSamplingRate = 18;

    #endregion

    #region Public Surface

    /// <summary>
    /// Scope monitor for real-time visualisation of the electrode (EMG / EEG) channels.
    /// Bind to a <c>ScopeMonitorView</c> in the UI layer.
    /// </summary>
    public ScopeMonitor ScopeEmg { get; } = new();

    /// <summary>
    /// Scope monitor for real-time visualisation of the IMU quaternion channels.
    /// Only populated when <see cref="StreamImu"/> is <see langword="true"/>.
    /// </summary>
    public ScopeMonitor ScopeImu { get; } = new();

    /// <summary>
    /// Observable telemetry counters (frames collected, packets lost, pipeline status)
    /// suitable for binding in a device card view.
    /// </summary>
    public Components.Devices.Muovi.MuoviStreamInfo StreamInfo { get; } = new();

    /// <summary>
    /// Zero-based indices of the Muovi probes to enable on the SyncStation.
    /// The index corresponds to the physical slot on the SyncStation (0–3 for standard
    /// Muovi probes, 4–15 for Muovi+ and other OT Bioelettronica devices). Verify the
    /// correct index against the LED indicators on the SyncStation.
    /// </summary>
    public List<int> ProbeIndices { get; } = new();

    /// <summary>
    /// Gets or sets the signal acquisition mode for all enabled probes.
    /// Must be set before calling <see cref="Connect"/>.
    /// </summary>
    /// <value>The default is <see cref="MuoviSignalMode.Emg"/>.</value>
    public MuoviSignalMode SignalMode { get; set; } = MuoviSignalMode.Emg;

    /// <summary>
    /// Gets or sets whether the four IMU quaternion channels (W, X, Y, Z) per probe
    /// are included in the output vector. Must be set before calling <see cref="Connect"/>.
    /// </summary>
    public bool StreamImu { get; set; }

    /// <summary>
    /// Explicit override for the publish strategy, set from the JSON <c>"matrix"</c> /
    /// <c>"vector"</c> param. When <see langword="null"/> (default), the mode is auto-derived
    /// from <see cref="BaseBlock.DesiredRate"/> vs the probe's nominal rate (legacy behaviour):
    /// batch into a <see cref="Matrix{T}"/> when more than one sample falls in an output period,
    /// otherwise publish single <see cref="Vector{T}"/> samples. When set, it forces the mode:
    /// <list type="bullet">
    ///   <item><description><see langword="true"/> — always publish a per-packet <see cref="Matrix{T}"/>
    ///   (every acquired sample is kept; correct for high-rate display/recording).</description></item>
    ///   <item><description><see langword="false"/> — always publish decimated single
    ///   <see cref="Vector{T}"/> samples at <see cref="BaseBlock.DesiredRate"/>.</description></item>
    /// </list>
    /// Must be set before <see cref="Connect"/>.
    /// </summary>
    public bool? ForceMatrixMode { get; set; }

    /// <summary>
    /// Number of output channels per probe: 32 electrode channels, plus optionally 4 IMU channels.
    /// </summary>
    public int ChannelsPerProbeOutput => MuoviConstants.ElectrodeChannels + (StreamImu ? MuoviConstants.ImuChannels : 0);

    /// <summary>
    /// Total number of output channels across all enabled probes.
    /// Equals <c><see cref="ProbeIndices"/>.Count × <see cref="ChannelsPerProbeOutput"/></c>.
    /// </summary>
    public int OutputChannels => ProbeIndices.Count * ChannelsPerProbeOutput;

    /// <summary>All output channel labels in order (EMG/EEG then IMU, repeated per probe).</summary>
    public List<string> ChannelLabels { get; private set; } = new();

    /// <summary>Electrode-only channel labels, used to populate the EMG/EEG scope legend.</summary>
    public List<string> EmgChannelLabels { get; private set; } = new();

    /// <summary>IMU-only channel labels, used to populate the IMU scope legend.</summary>
    public List<string> ImuChannelLabels { get; private set; } = new();

    /// <summary>Indicates whether electrode data is available (always <see langword="true"/> while streaming).</summary>
    [ObservableProperty] private bool _hasEmg;

    /// <summary>Indicates whether IMU data is available (mirrors <see cref="StreamImu"/> while streaming).</summary>
    [ObservableProperty] private bool _hasImu;

    #endregion

    #region Internal State

    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _readerThread;
    private volatile bool _reading;

    private int _totalRawColumns;
    private int _bytesPerSample;
    private int _bytesPerPacket;
    private int _nominalRate;
    private double _conversionFactor;
    private bool _matrixMode;
    private int _samplesPerBatch;

    private volatile Vector<double>? _latestSample;
    private volatile Vector<double>? _latestImu;
    private volatile bool _sampleReady;
    private short _lastRamp;
    private bool _rampInitialized;
    private volatile bool _isStreaming;

    /// <summary>Indicates whether the block is currently connected to the SyncStation and streaming data.</summary>
    public bool IsStreaming => _isStreaming;

    #endregion

    #region Construction / Factory

    /// <summary>
    /// Initialises a new instance of the <see cref="Muovi"/> class with default settings.
    /// </summary>
    /// <param name="name">Display name for this block instance.</param>
    /// <param name="desiredRate">
    /// Desired output rate in Hz. If lower than the probe's nominal rate, multiple samples
    /// are batched into a <see cref="Matrix{T}"/>. If equal, single-sample
    /// <see cref="Vector{T}"/> mode is used.
    /// </param>
    public Muovi(string name = "Muovi", double desiredRate = 100) : base(name, desiredRate)
    {
        SignalRate = 2000;
        ScopeEmg.ScopeInit(); ScopeImu.ScopeInit();
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_muovi.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_muovi";

    /// <summary>
    /// Factory method that creates and configures a <see cref="Muovi"/> instance
    /// from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">The application's dependency injection service provider.</param>
    /// <param name="m">
    /// The JSON model containing <c>Name</c>, <c>DesiredRate</c>, <c>Path</c> (for CSV recording),
    /// and <c>Params</c> (probe indices and mode keywords).
    /// </param>
    /// <returns>A fully configured <see cref="Muovi"/> block ready to be connected.</returns>
    /// <remarks>
    /// <para>Recognised <c>Params</c> values:</para>
    /// <list type="bullet">
    ///   <item><description>Integer values — interpreted as zero-based probe slot indices.</description></item>
    ///   <item><description><c>"emg"</c> — standard EMG mode (default).</description></item>
    ///   <item><description><c>"emg_lowgain"</c> or <c>"lowgain"</c> — EMG with reduced gain.</description></item>
    ///   <item><description><c>"eeg"</c> — EEG acquisition mode at 500 Hz.</description></item>
    ///   <item><description><c>"imu"</c> — include IMU quaternion channels in output.</description></item>
    /// </list>
    /// </remarks>
    public static Muovi ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "Muovi";
        var rate = m.DesiredRate ?? 100;
        var block = ActivatorUtilities.CreateInstance<Muovi>(sp, name, rate);

        if (m.Params is { Count: > 0 })
            foreach (var p in m.Params)
            {
                var s = p?.ToString()?.Trim();
                if (s is null) continue;
                if (int.TryParse(s, out int idx)) { block.ProbeIndices.Add(idx); continue; }
                switch (s.ToLowerInvariant())
                {
                    case "eeg": block.SignalMode = MuoviSignalMode.Eeg; break;
                    case "emg_lowgain" or "lowgain": block.SignalMode = MuoviSignalMode.EmgLowGain; break;
                    case "emg": block.SignalMode = MuoviSignalMode.Emg; break;
                    case "imu": block.StreamImu = true; break;
                    case "matrix" or "matrixmode" or "batch": block.ForceMatrixMode = true; break;
                    case "vector" or "vectormode" or "single": block.ForceMatrixMode = false; break;
                }
            }
        if (block.ProbeIndices.Count == 0) block.ProbeIndices.Add(0);
        return block;
    }

    #endregion

    #region Lifecycle

    /// <summary>
    /// Opens a TCP connection to the SyncStation, sends the configuration packet,
    /// and starts the background reader thread.
    /// </summary>
    /// <remarks>
    /// Set <see cref="SignalMode"/>, <see cref="StreamImu"/>, and <see cref="ProbeIndices"/>
    /// before calling this method. The SyncStation begins transmitting data immediately
    /// after the configuration packet is acknowledged. The method is a no-op if the block
    /// is already streaming — call <see cref="Disconnect"/> first to reconfigure.
    /// </remarks>
    /// <exception cref="SocketException">
    /// Thrown if the TCP connection to the SyncStation cannot be established
    /// (e.g. device not powered on or Wi-Fi not connected).
    /// </exception>
    public void Connect()
    {
        if (_isStreaming) return;

        _nominalRate      = MuoviConstants.NominalRate(SignalMode);
        _conversionFactor = MuoviConstants.ConversionFactor(SignalMode);
        _totalRawColumns  = ProbeIndices.Count * MuoviConstants.ChannelsPerProbeRaw + SyncStationExtraCh;
        _bytesPerSample   = _totalRawColumns * 2;
        _bytesPerPacket   = _bytesPerSample * SubSamplingRate;
        _samplesPerBatch  = Math.Max(1, _nominalRate / (int)Math.Max(1, DesiredRate));
        // Explicit override (JSON "matrix"/"vector") wins; otherwise auto-derive from rate.
        _matrixMode       = ForceMatrixMode ?? (_samplesPerBatch > 1);
        // In forced-matrix mode with DesiredRate == nominal, _samplesPerBatch would be 1;
        // a 1-row matrix is still valid (one row per packet) and keeps every sample.
        BuildChannelLabels();

        // Tell the scopes the rate at which samples are actually fed so the buffer size
        // and time-axis spacing (DataStreamer.Period = 1/SignalRate) match the real stream.
        // Matrix mode pushes every acquired sample via EnqueueBatch at the nominal rate;
        // vector mode publishes one decimated sample per timer tick at DesiredRate. Using
        // _nominalRate in vector mode makes the streamer expect ~20× more samples than
        // arrive, so the trace creeps far too slowly and the scope looks frozen.
        double scopeRate = _matrixMode ? _nominalRate : DesiredRate;
        ScopeEmg.UpdateSignalRate(scopeRate);
        if (StreamImu) ScopeImu.UpdateSignalRate(scopeRate);

        Console.WriteLine($"[Muovi] Probes=[{string.Join(",", ProbeIndices)}], Mode={SignalMode}, " +
                          $"IMU={StreamImu}, TotalRawCols={_totalRawColumns}, OutputCh={OutputChannels}");
        Console.WriteLine($"[Muovi] bytes/sample={_bytesPerSample}, bytes/packet={_bytesPerPacket} " +
                          $"(18 samples), Output={(_matrixMode ? $"Matrix({_samplesPerBatch}) push" : "Vector timer")}");

        _client = new TcpClient(SyncStationIp, SyncStationPort);
        _client.ReceiveBufferSize = 65536; _client.NoDelay = true;
        _stream = _client.GetStream();
        SendConfiguration();

        _reading = true; _sampleReady = false;
        _latestSample = null; _latestImu = null;
        _lastRamp = 0; _rampInitialized = false;

        _readerThread = new Thread(ReaderLoop)
        { IsBackground = true, Name = "Muovi-Reader", Priority = ThreadPriority.AboveNormal };
        _readerThread.Start();

        _isStreaming = true; HasEmg = true; HasImu = StreamImu;
        StreamInfo.DeviceName = "Muovi SyncStation";
        StreamInfo.ProbesConnected = ProbeIndices.Count;
        StreamInfo.TotalChannels = OutputChannels;
        StreamInfo.PipelineStatus = "Streaming";
        Console.WriteLine("[Muovi] Connected and streaming.");
    }

    /// <summary>
    /// Asynchronously connects to the SyncStation on a background thread.
    /// </summary>
    /// <returns>A task that completes when the connection is established and streaming has started.</returns>
    public Task ConnectAsync() => Task.Run(Connect);

    /// <summary>
    /// Builds and sends the SyncStation configuration packet over the open TCP stream.
    /// </summary>
    /// <remarks>
    /// <para>Packet format per OT Bioelettronica SyncStation protocol v2.8:</para>
    /// <list type="number">
    ///   <item><description>
    ///     <strong>START byte A:</strong> <c>[0][REC_ON=0][SIZE4..SIZE0][GO=1]</c> where
    ///     SIZE is the count of CONTROL bytes that follow.
    ///   </description></item>
    ///   <item><description>
    ///     <strong>CONTROL bytes</strong> (one per enabled probe):
    ///     <c>[DEV3..DEV0][EMG/EEG][MODE1][MODE0][EN=1]</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <strong>CRC-8</strong> trailer computed over all preceding bytes.
    ///   </description></item>
    /// </list>
    /// </remarks>
    private void SendConfiguration()
    {
        if (_stream is null) return;

        int[] deviceEN = new int[16];
        int[] mode     = new int[16];

        foreach (int idx in ProbeIndices)
            if (idx is >= 0 and < 16)
            {
                deviceEN[idx] = 1;
                mode[idx]     = SignalMode == MuoviSignalMode.EmgLowGain ? 1 : 0;
            }

        int sizeComm = deviceEN.Sum();
        var configStr = new byte[18];
        configStr[0] = (byte)(sizeComm * 2 + 1);

        int pos = 1;
        for (int i = 0; i < 16; i++)
        {
            if (deviceEN[i] == 1)
            {
                int emgBit = (SignalMode == MuoviSignalMode.Eeg) ? 0 : 1;
                configStr[pos] = (byte)(i * 16 + emgBit * 8 + mode[i] * 2 + 1);
                pos++;
            }
        }

        configStr[pos] = MuoviConstants.Crc8(configStr, pos - 1);
        int packetLen = pos + 1;
        _stream.Write(configStr, 0, packetLen);

        Console.WriteLine($"[Muovi] Config sent ({packetLen} bytes): " +
                          string.Join(" ", Enumerable.Range(0, packetLen).Select(i => $"0x{configStr[i]:X2}")));
    }

    /// <summary>
    /// Stops the reader thread, sends a shutdown packet to the SyncStation,
    /// and closes the TCP connection.
    /// </summary>
    /// <remarks>
    /// Safe to call even if the block is not currently streaming (no-op in that case).
    /// The shutdown packet (<c>0x00</c> + CRC) instructs the SyncStation firmware to
    /// stop transmitting.
    /// </remarks>
    public void Disconnect()
    {
        if (!_isStreaming) return;
        _reading = false; _readerThread?.Join(2000);
        if (_stream is not null)
        {
            try
            {
                var shutdown = new byte[2];
                shutdown[0] = 0x00;
                shutdown[1] = MuoviConstants.Crc8(shutdown, 0);
                _stream.Write(shutdown, 0, 2);
            }
            catch { }
        }
        _stream?.Close(); _client?.Close(); _stream = null; _client = null;
        _isStreaming = false; HasEmg = false; HasImu = false;
        StreamInfo.PipelineStatus = "Disconnected";
        Console.WriteLine("[Muovi] Disconnected.");
    }

    /// <summary>
    /// Releases all resources: pauses and disposes both scope monitors,
    /// disconnects from the SyncStation, and flushes the CSV dumper.
    /// </summary>
    public override void Dispose()
    {
        try { ScopeEmg.Pause(); ScopeEmg.Dispose(); } catch { }
        try { ScopeImu.Pause(); ScopeImu.Dispose(); } catch { }
        Disconnect(); base.Dispose();
    }

    #endregion

    #region Channel Labels

    /// <summary>
    /// Rebuilds <see cref="ChannelLabels"/>, <see cref="EmgChannelLabels"/>,
    /// and <see cref="ImuChannelLabels"/> based on the current
    /// <see cref="ProbeIndices"/>, <see cref="SignalMode"/>, and <see cref="StreamImu"/> settings.
    /// </summary>
    private void BuildChannelLabels()
    {
        ChannelLabels.Clear(); EmgChannelLabels.Clear(); ImuChannelLabels.Clear();
        string prefix = MuoviConstants.ChannelPrefix(SignalMode);
        foreach (int pi in ProbeIndices)
        {
            string tag = ProbeIndices.Count > 1 ? $"P{pi + 1} " : "";
            for (int ch = 0; ch < MuoviConstants.ElectrodeChannels; ch++)
            { var l = $"{tag}{prefix} {ch + 1}"; ChannelLabels.Add(l); EmgChannelLabels.Add(l); }
            if (StreamImu)
                foreach (var q in new[] { "Quat W", "Quat X", "Quat Y", "Quat Z" })
                { var l = $"{tag}{q}"; ChannelLabels.Add(l); ImuChannelLabels.Add(l); }
        }
    }

    #endregion

    #region Reader Thread

    /// <summary>
    /// Background thread loop that continuously reads from the TCP stream, accumulates bytes
    /// into complete frames, and dispatches decoded data to the pipeline.
    /// </summary>
    /// <remarks>
    /// The SyncStation sends data in bursts of <see cref="SubSamplingRate"/> (18) samples.
    /// TCP may fragment or coalesce these bursts, so the reader uses an accumulation buffer
    /// and processes as many complete frames as are available after each read.
    /// </remarks>
    private void ReaderLoop()
    {
        var accum = new byte[_bytesPerPacket * 4 + 65536];
        int accumCount = 0;

        Console.WriteLine($"[Muovi] Reader started, {_bytesPerSample} bytes/sample, " +
                          $"{_bytesPerPacket} bytes/packet (18 samples)");

        while (_reading && _stream is not null)
        {
            try
            {
                int space = accum.Length - accumCount;
                if (space <= 0) { accumCount = 0; continue; }
                int bytesRead = _stream.Read(accum, accumCount, space);
                if (bytesRead <= 0) break;
                accumCount += bytesRead;

                if (_matrixMode)
                {
                    int batchBytes = _bytesPerSample * _samplesPerBatch;
                    int consumed = 0;
                    while (consumed + batchBytes <= accumCount)
                    { PublishMatrixBatch(accum, consumed); consumed += batchBytes; StreamInfo.FramesCollected++; }
                    int left = accumCount - consumed;
                    if (left > 0 && consumed > 0) Buffer.BlockCopy(accum, consumed, accum, 0, left);
                    accumCount = left;
                }
                else
                {
                    int consumed = 0;
                    while (consumed + _bytesPerSample <= accumCount)
                    { DecodeSampleInline(accum, consumed); CheckRamp(accum, consumed); consumed += _bytesPerSample; StreamInfo.FramesCollected++; }
                    int left = accumCount - consumed;
                    if (left > 0 && consumed > 0) Buffer.BlockCopy(accum, consumed, accum, 0, left);
                    accumCount = left;
                }
            }
            catch (Exception ex)
            {
                if (_reading) Log.Error("Muovi", Name, ex, "Reader error.");
                break;
            }
        }
        Console.WriteLine("[Muovi] Reader thread exited.");
    }

    /// <summary>
    /// Decodes a single sample from the raw byte frame into the latest
    /// <see cref="Vector{T}"/> and optional IMU vector (vector / timer mode).
    /// </summary>
    /// <param name="frame">The accumulation buffer containing raw bytes.</param>
    /// <param name="offset">Byte offset of the sample within <paramref name="frame"/>.</param>
    private void DecodeSampleInline(byte[] frame, int offset)
    {
        var vec = Vector<double>.Build.Dense(OutputChannels);
        Vector<double>? imu = StreamImu ? Vector<double>.Build.Dense(ProbeIndices.Count * MuoviConstants.ImuChannels) : null;

        for (int p = 0; p < ProbeIndices.Count; p++)
        {
            int rawBase = offset + p * MuoviConstants.ChannelsPerProbeRaw * 2;
            int outBase = p * ChannelsPerProbeOutput;
            for (int ch = 0; ch < MuoviConstants.ElectrodeChannels; ch++)
            { int i = rawBase + ch * 2; vec[outBase + ch] = (short)(frame[i] | (frame[i + 1] << 8)) * _conversionFactor; }
            if (StreamImu)
                for (int q = 0; q < MuoviConstants.ImuChannels; q++)
                { int i = rawBase + (MuoviConstants.ElectrodeChannels + q) * 2; short raw = (short)(frame[i] | (frame[i + 1] << 8)); vec[outBase + MuoviConstants.ElectrodeChannels + q] = raw; imu![p * MuoviConstants.ImuChannels + q] = raw; }
        }
        _latestSample = vec; _latestImu = imu; _sampleReady = true;
    }

    /// <summary>
    /// Decodes a batch of consecutive samples into a <see cref="Matrix{T}"/>
    /// and publishes it downstream (matrix / push mode).
    /// </summary>
    /// <param name="buf">The accumulation buffer containing raw bytes.</param>
    /// <param name="batchOffset">Byte offset of the first sample in the batch.</param>
    private void PublishMatrixBatch(byte[] buf, int batchOffset)
    {
        var matrix = Matrix<double>.Build.Dense(_samplesPerBatch, OutputChannels);
        Vector<double>? lastImu = null;

        for (int s = 0; s < _samplesPerBatch; s++)
        {
            int sOff = batchOffset + s * _bytesPerSample;
            Vector<double>? sampleImu = StreamImu
                ? Vector<double>.Build.Dense(ProbeIndices.Count * MuoviConstants.ImuChannels) : null;

            for (int p = 0; p < ProbeIndices.Count; p++)
            {
                int rawBase = sOff + p * MuoviConstants.ChannelsPerProbeRaw * 2;
                int outBase = p * ChannelsPerProbeOutput;

                for (int ch = 0; ch < MuoviConstants.ElectrodeChannels; ch++)
                {
                    int i = rawBase + ch * 2;
                    matrix[s, outBase + ch] = (short)(buf[i] | (buf[i + 1] << 8)) * _conversionFactor;
                }

                if (StreamImu)
                    for (int q = 0; q < MuoviConstants.ImuChannels; q++)
                    {
                        int i = rawBase + (MuoviConstants.ElectrodeChannels + q) * 2;
                        short raw = (short)(buf[i] | (buf[i + 1] << 8));
                        matrix[s, outBase + MuoviConstants.ElectrodeChannels + q] = raw;
                        sampleImu![p * MuoviConstants.ImuChannels + q] = raw;
                    }
            }
            if (sampleImu is not null) lastImu = sampleImu;
            CheckRamp(buf, sOff);
        }

        var lastRow = matrix.Row(_samplesPerBatch - 1);
        _latestSample = lastRow;

        // Feed the WHOLE batch to the scope (not just the last row) so every acquired
        // sample is plotted at 1/SignalRate spacing. Using EnqueueData(lastRow) here would
        // drop _samplesPerBatch-1 of every _samplesPerBatch samples and run the time-axis
        // ~_samplesPerBatch× too slow — the matrix/batch path exists precisely to avoid that.
        // that many NaN rows here before the real batch so the scope shows a proportional
        // break in the trace (DataStreamer renders NaN as a line break).
        ScopeEmg.EnqueueBatch(ExtractElectrodeMatrix(matrix));
        if (StreamImu && lastImu is not null) { _latestImu = lastImu; ScopeImu.EnqueueData(lastImu); }
        Publish(matrix);
    }

    /// <summary>
    /// Extracts only the electrode channels from a full output vector that may contain
    /// interleaved IMU channels, producing a contiguous EMG/EEG-only vector for
    /// <see cref="ScopeEmg"/>.
    /// </summary>
    /// <param name="full">The full output vector including both electrode and IMU channels.</param>
    /// <returns>
    /// A vector containing only electrode channels. If <see cref="StreamImu"/> is
    /// <see langword="false"/>, returns <paramref name="full"/> directly without copying.
    /// </returns>
    private Vector<double> ExtractElectrode(Vector<double> full)
    {
        if (!StreamImu) return full;
        int totalEmg = ProbeIndices.Count * MuoviConstants.ElectrodeChannels;
        var emg = Vector<double>.Build.Dense(totalEmg);
        for (int p = 0; p < ProbeIndices.Count; p++)
            for (int ch = 0; ch < MuoviConstants.ElectrodeChannels; ch++)
                emg[p * MuoviConstants.ElectrodeChannels + ch] = full[p * ChannelsPerProbeOutput + ch];
        return emg;
    }

    /// <summary>
    /// Matrix counterpart of <see cref="ExtractElectrode"/>: returns a
    /// [rows × (probes·electrodeChannels)] sub-matrix containing only the electrode
    /// columns, stripping the interleaved IMU columns. When <see cref="StreamImu"/> is
    /// <see langword="false"/> the matrix is returned unchanged (no copy).
    /// </summary>
    private Matrix<double> ExtractElectrodeMatrix(Matrix<double> full)
    {
        if (!StreamImu) return full;
        int rows = full.RowCount;
        int totalEmg = ProbeIndices.Count * MuoviConstants.ElectrodeChannels;
        var emg = Matrix<double>.Build.Dense(rows, totalEmg);
        for (int p = 0; p < ProbeIndices.Count; p++)
            for (int ch = 0; ch < MuoviConstants.ElectrodeChannels; ch++)
            {
                int src = p * ChannelsPerProbeOutput + ch;
                int dst = p * MuoviConstants.ElectrodeChannels + ch;
                for (int r = 0; r < rows; r++)
                    emg[r, dst] = full[r, src];
            }
        return emg;
    }

    #endregion

    #region Timer Tick (Vector mode only)

    public override int MinInputs => 0;
    public override int MaxInputs => 0;

    /// <summary>
    /// Called by the upstream timer block. In vector mode, publishes the most recently
    /// decoded sample to downstream blocks and scope monitors.
    /// </summary>
    /// <param name="sender">The timer block that triggered this callback.</param>
    /// <param name="tNow">The current pipeline timestamp.</param>
    /// <remarks>
    /// This method is a no-op in matrix mode, where data is published directly
    /// from <see cref="PublishMatrixBatch"/>.
    /// </remarks>
    protected override void OnReceive(object sender, object tNow)
    {
        if (_matrixMode || !_sampleReady) return;
        var vec = _latestSample;
        if (vec is null) return;
        Publish(vec);
        ScopeEmg.EnqueueData(ExtractElectrode(vec));
        if (StreamImu) { var imu = _latestImu; if (imu is not null) ScopeImu.EnqueueData(imu); }
    }

    #endregion

    #region Ramp Check

    /// <summary>
    /// Validates the per-sample ramp counter embedded in the last raw column of each frame.
    /// Sets <see cref="MuoviStreamInfo.DataLoss"/> when a gap is detected and resets it
    /// when consecutive clean samples arrive.
    /// </summary>
    /// <param name="frame">The raw byte buffer.</param>
    /// <param name="offset">Byte offset of the current sample within <paramref name="frame"/>.</param>
    /// <remarks>
    /// The SyncStation ramp counter increments by 256 per firmware packet (not per sample).
    /// A gap of 257 at the 16-bit wrap boundary is normal; gaps ≥ 258 indicate packet loss.
    /// </remarks>
    private void CheckRamp(byte[] frame, int offset)
    {
        int rampIdx = offset + (_totalRawColumns - 1) * 2;
        if (rampIdx + 1 >= frame.Length) return;
        short rampVal = (short)(frame[rampIdx] | (frame[rampIdx + 1] << 8));
        if (_rampInitialized)
        {
            int gap = (ushort)((ushort)rampVal - (ushort)_lastRamp);
            if (gap > 257)
            {
                StreamInfo.DataLoss = true; StreamInfo.PacketsLost++;
                if (StreamInfo.PacketsLost <= 20)
                    Console.WriteLine($"[Muovi] Data loss: ramp {_lastRamp} -> {rampVal} (gap {gap})");

                // (gap/256 - 1) ≈ number of lost packets, ×SubSamplingRate ≈ lost samples.
                // Surface that lost-sample count to the publish path and insert that many
                // NaN rows into ScopeEmg via EnqueueBatch so the scope renders a real gap
                // (ScottPlot DataStreamer breaks the line on NaN). Key it off THIS ramp gap,
                // not arrival timing, so a late-but-complete burst correctly shows no gap.
            }
            else { StreamInfo.DataLoss = false; }
        }
        _lastRamp = rampVal; _rampInitialized = true;
    }

    #endregion
}