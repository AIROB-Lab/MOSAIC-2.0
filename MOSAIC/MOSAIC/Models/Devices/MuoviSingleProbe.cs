using System;
using System.Collections.Generic;
using System.Net;
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
/// from a single OT Bioelettronica Muovi probe over a direct Wi-Fi TCP connection.
/// </summary>
/// <example>
/// <para>Block entry for a larger pipeline. Params are keyword tokens: emg selects the signal mode; matrix requests batched output. Configure the Wi-Fi probe to connect to this host before starting acquisition.</para>
/// <code language="json">
/// {
///   "MuoviSingleProbe": {
///     "Type": "muovisingleprobe",
///     "Inputs": [],
///     "Params": ["emg", "matrix"]
///   }
/// }
/// </code>
/// </example>
public sealed partial class MuoviSingleProbe : BaseBlock
{
    /// <inheritdoc />
    public override int MinInputs => 0;

    /// <inheritdoc />
    public override int MaxInputs => 0;

    #region Constants

    /// <summary>TCP port on which this block listens for the incoming probe connection.</summary>
    private const int ListenPort = 54321;

    /// <summary>Total raw columns per frame (32 electrode + 4 IMU + 2 accessory).</summary>
    private const int TotalColumnsPerFrame = MuoviConstants.ChannelsPerProbeRaw;  // 38

    /// <summary>Byte count for one sample frame: <c>38 columns × 2 bytes</c> = 76 bytes.</summary>
    private const int BytesPerSample = TotalColumnsPerFrame * 2;

    #endregion

    #region Public Surface

    /// <summary>Scope monitor for real-time visualisation of the electrode (EMG / EEG) channels.</summary>
    public ScopeMonitor ScopeEmg { get; } = new();

    /// <summary>Scope monitor for real-time visualisation of the IMU quaternion channels.</summary>
    public ScopeMonitor ScopeImu { get; } = new();

    /// <summary>Observable telemetry counters for binding in a device card view.</summary>
    public Components.Devices.Muovi.MuoviStreamInfo StreamInfo { get; } = new();

    /// <summary>Signal acquisition mode. Must be set before <see cref="Connect"/>.</summary>
    public MuoviSignalMode SignalMode { get; set; } = MuoviSignalMode.Emg;

    /// <summary>Whether the four IMU quaternion channels are included.</summary>
    public bool StreamImu { get; set; }

    /// <summary>Number of output channels: 32 electrode, plus optionally 4 IMU.</summary>
    public int OutputChannels => MuoviConstants.ElectrodeChannels + (StreamImu ? MuoviConstants.ImuChannels : 0);

    public List<string> ChannelLabels { get; private set; } = new();
    public List<string> EmgChannelLabels { get; private set; } = new();
    public List<string> ImuChannelLabels { get; private set; } = new();

    [ObservableProperty] private bool _hasEmg;
    [ObservableProperty] private bool _hasImu;

    /// <summary>
    /// Explicit override for the publish strategy (JSON <c>"matrix"</c> / <c>"vector"</c>).
    /// <see langword="null"/> (default) keeps the legacy auto-derivation from
    /// <see cref="BaseBlock.DesiredRate"/>; <see langword="true"/> forces per-packet
    /// <see cref="Matrix{T}"/> push (lossless), <see langword="false"/> forces decimated
    /// single-<see cref="Vector{T}"/> output. Must be set before <see cref="Connect"/>.
    /// </summary>
    public bool? ForceMatrixMode { get; set; }

    #endregion

    #region Internal State

    private Socket? _listenSocket;
    private Socket? _clientSocket;
    private NetworkStream? _stream;
    private Thread? _readerThread;
    private volatile bool _reading;

    private int _nominalRate;
    private double _conversionFactor;
    private byte _configByte;
    private bool _matrixMode;
    private int _samplesPerBatch;

    private volatile Vector<double>? _latestSample;
    private volatile Vector<double>? _latestImu;
    private volatile bool _sampleReady;
    private short _lastRamp;
    private bool _rampInitialized;
    private volatile bool _isStreaming;

    public bool IsStreaming => _isStreaming;

    #endregion

    #region Construction / Factory

    public MuoviSingleProbe(string name = "MuoviSingle", double desiredRate = 100)
    {
        Name = name; DesiredRate = desiredRate;
        ScopeEmg.ScopeInit(); ScopeImu.ScopeInit();
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_muovi_single.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_muovi_single";

    public static MuoviSingleProbe ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "MuoviSingle";
        var rate = m.DesiredRate ?? 100;
        var block = ActivatorUtilities.CreateInstance<MuoviSingleProbe>(sp, name, rate);

        if (m.Params is { Count: > 0 })
            foreach (var p in m.Params)
                switch (p?.ToString()?.Trim().ToLowerInvariant())
                {
                    case "eeg": block.SignalMode = MuoviSignalMode.Eeg; break;
                    case "emg_lowgain" or "lowgain": block.SignalMode = MuoviSignalMode.EmgLowGain; break;
                    case "emg": block.SignalMode = MuoviSignalMode.Emg; break;
                    case "imu": block.StreamImu = true; break;
                    case "matrix" or "matrixmode" or "batch": block.ForceMatrixMode = true; break;
                    case "vector" or "vectormode" or "single": block.ForceMatrixMode = false; break;
                }
        return block;
    }

    #endregion

    #region Lifecycle

    public void Connect()
    {
        if (_isStreaming) return;

        _configByte       = MuoviConstants.BuildConfigByte(SignalMode);
        _nominalRate      = MuoviConstants.NominalRate(SignalMode);
        _conversionFactor = MuoviConstants.ConversionFactor(SignalMode);
        _samplesPerBatch  = Math.Max(1, _nominalRate / (int)Math.Max(1, DesiredRate));
        _matrixMode       = ForceMatrixMode ?? (_samplesPerBatch > 1);
        BuildChannelLabels();

        Console.WriteLine($"[MuoviSingle] Mode={SignalMode}, IMU={StreamImu}, " +
                          $"Config=0x{_configByte:X2}, OutputCh={OutputChannels}, " +
                          $"Output={(_matrixMode ? $"Matrix({_samplesPerBatch}) push" : "Vector timer")}");

        StreamInfo.DeviceName = "Muovi Single Probe";
        StreamInfo.PipelineStatus = "Waiting for probe...";

        _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listenSocket.Bind(new IPEndPoint(IPAddress.Any, ListenPort));
        _listenSocket.Listen(1);

        Console.WriteLine($"[MuoviSingle] Listening on port {ListenPort}...");
        _clientSocket = _listenSocket.Accept();
        _clientSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
        _clientSocket.ReceiveBufferSize = 65536;
        Console.WriteLine($"[MuoviSingle] Probe connected from {_clientSocket.RemoteEndPoint}");

        _stream = new NetworkStream(_clientSocket);
        _stream.WriteByte(_configByte);

        _reading = true; _sampleReady = false;
        _latestSample = null; _latestImu = null;
        _lastRamp = 0; _rampInitialized = false;

        _readerThread = new Thread(ReaderLoop)
        { IsBackground = true, Name = "MuoviSingle-Reader", Priority = ThreadPriority.AboveNormal };
        _readerThread.Start();

        _isStreaming = true;
        HasEmg = true;
        HasImu = StreamImu;
        StreamInfo.ProbesConnected = 1;
        StreamInfo.TotalChannels = OutputChannels;
        StreamInfo.PipelineStatus = "Streaming";

        // Tell the scopes the rate at which samples are actually fed so buffers and
        // time-axis match. Matrix mode pushes every acquired sample via EnqueueBatch at
        // the nominal rate; vector mode publishes one decimated sample per timer tick at
        // DesiredRate. Using _nominalRate in vector mode makes the streamer expect ~20×
        // more samples than arrive, so the trace creeps far too slowly and looks frozen.
        double scopeRate = _matrixMode ? _nominalRate : DesiredRate;
        ScopeEmg.UpdateSignalRate(scopeRate);
        if (StreamImu) ScopeImu.UpdateSignalRate(scopeRate);
    }

    public Task ConnectAsync() => Task.Run(Connect);

    public void Disconnect()
    {
        if (!_isStreaming) return;
        if (_stream is not null) try { _stream.WriteByte(0x00); } catch { }
        _reading = false; _readerThread?.Join(2000);
        _stream?.Close(); _clientSocket?.Close(); _listenSocket?.Close();
        _stream = null; _clientSocket = null; _listenSocket = null;
        _isStreaming = false;
        HasEmg = false;
        HasImu = false;
        StreamInfo.PipelineStatus = "Disconnected";
        Console.WriteLine("[MuoviSingle] Disconnected.");
    }

    public override void Dispose()
    {
        try { ScopeEmg.Pause(); ScopeEmg.Dispose(); } catch { }
        try { ScopeImu.Pause(); ScopeImu.Dispose(); } catch { }
        Disconnect(); base.Dispose();
    }

    #endregion

    #region Channel Labels

    private void BuildChannelLabels()
    {
        ChannelLabels.Clear(); EmgChannelLabels.Clear(); ImuChannelLabels.Clear();
        string prefix = MuoviConstants.ChannelPrefix(SignalMode);
        for (int ch = 0; ch < MuoviConstants.ElectrodeChannels; ch++)
        { var l = $"{prefix} {ch + 1}"; ChannelLabels.Add(l); EmgChannelLabels.Add(l); }
        if (StreamImu)
            foreach (var l in new[] { "Quat W", "Quat X", "Quat Y", "Quat Z" })
            { ChannelLabels.Add(l); ImuChannelLabels.Add(l); }
    }

    /// <summary>
    /// Returns a [rows × electrodeChannels] sub-matrix containing only the
    /// electrode columns. When <see cref="StreamImu"/> is <see langword="false"/>
    /// the input matrix is returned unchanged.
    /// </summary>
    private Matrix<double> ExtractElectrodeMatrix(Matrix<double> full)
    {
        if (!StreamImu) return full;
        return full.SubMatrix(0, full.RowCount, 0, MuoviConstants.ElectrodeChannels);
    }

    #endregion

    #region Reader Thread

    private void ReaderLoop()
    {
        int batchBytes = _matrixMode ? BytesPerSample * _samplesPerBatch : BytesPerSample;
        var accum = new byte[batchBytes + 65536];
        int accumCount = 0;

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
                    while (consumed + BytesPerSample <= accumCount)
                    { DecodeSampleInline(accum, consumed); CheckRamp(accum, consumed); consumed += BytesPerSample; StreamInfo.FramesCollected++; }
                    int left = accumCount - consumed;
                    if (left > 0 && consumed > 0) Buffer.BlockCopy(accum, consumed, accum, 0, left);
                    accumCount = left;
                }
            }
            catch (Exception ex)
            {
                if (_reading) Log.Error("MuoviSingle", Name, ex, "Reader error.");
                break;
            }
        }
        Console.WriteLine("[MuoviSingle] Reader thread exited.");
    }

    private static short ReadInt16BigEndian(byte[] buf, int offset)
        => (short)((buf[offset] << 8) | buf[offset + 1]);

    private void DecodeSampleInline(byte[] frame, int offset)
    {
        var vec = Vector<double>.Build.Dense(OutputChannels);
        for (int ch = 0; ch < MuoviConstants.ElectrodeChannels; ch++)
        {
            int i = offset + ch * 2;
            vec[ch] = ReadInt16BigEndian(frame, i) * _conversionFactor;
        }
        if (StreamImu)
        {
            var imu = Vector<double>.Build.Dense(MuoviConstants.ImuChannels);
            for (int q = 0; q < MuoviConstants.ImuChannels; q++)
            {
                int i = offset + (MuoviConstants.ElectrodeChannels + q) * 2;
                short raw = ReadInt16BigEndian(frame, i);
                vec[MuoviConstants.ElectrodeChannels + q] = raw;
                imu[q] = raw;
            }
            _latestImu = imu;
        }
        _latestSample = vec; _sampleReady = true;
    }

    private void PublishMatrixBatch(byte[] buf, int batchOffset)
    {
        var matrix = Matrix<double>.Build.Dense(_samplesPerBatch, OutputChannels);
        Vector<double>? lastImu = null;

        for (int s = 0; s < _samplesPerBatch; s++)
        {
            int off = batchOffset + s * BytesPerSample;
            for (int ch = 0; ch < MuoviConstants.ElectrodeChannels; ch++)
            {
                int i = off + ch * 2;
                matrix[s, ch] = ReadInt16BigEndian(buf, i) * _conversionFactor;
            }
            if (StreamImu)
            {
                var imu = Vector<double>.Build.Dense(MuoviConstants.ImuChannels);
                for (int q = 0; q < MuoviConstants.ImuChannels; q++)
                {
                    int i = off + (MuoviConstants.ElectrodeChannels + q) * 2;
                    short raw = ReadInt16BigEndian(buf, i);
                    matrix[s, MuoviConstants.ElectrodeChannels + q] = raw;
                    imu[q] = raw;
                }
                lastImu = imu;
            }
            CheckRamp(buf, off);
        }

        var lastRow = matrix.Row(_samplesPerBatch - 1);
        _latestSample = lastRow;

        // Feed full batch to scope (no per-sample throttle). IMU stays single-frame
        // since it's quaternion orientation, not a waveform.
        ScopeEmg.EnqueueBatch(ExtractElectrodeMatrix(matrix));
        if (StreamImu && lastImu is not null) { _latestImu = lastImu; ScopeImu.EnqueueData(lastImu); }

        Publish(matrix);
    }

    #endregion

    #region Timer Tick (Vector mode only)

    protected override void OnReceive(object sender, object tNow)
    {
        if (_matrixMode || !_sampleReady) return;
        var vec = _latestSample;
        if (vec is null) return;
        Publish(vec);
        ScopeEmg.EnqueueData(vec.SubVector(0, MuoviConstants.ElectrodeChannels));
        if (StreamImu) { var imu = _latestImu; if (imu is not null) ScopeImu.EnqueueData(imu); }
    }

    #endregion

    #region Ramp Check

    private void CheckRamp(byte[] frame, int offset)
    {
        int rampIdx = offset + (TotalColumnsPerFrame - 1) * 2;
        if (rampIdx + 1 >= frame.Length) return;
        short rampVal = ReadInt16BigEndian(frame, rampIdx);
        if (_rampInitialized)
        {
            int gap = (ushort)((ushort)rampVal - (ushort)_lastRamp);
            if (gap > 257)
            {
                StreamInfo.DataLoss = true; StreamInfo.PacketsLost++;
                if (StreamInfo.PacketsLost <= 20)
                    Console.WriteLine($"[MuoviSingle] Data loss: ramp {_lastRamp} -> {rampVal} (gap {gap})");
            }
            else { StreamInfo.DataLoss = false; }
        }
        _lastRamp = rampVal; _rampInitialized = true;
    }

    #endregion
}