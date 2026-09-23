using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Devices.Quattrocento;
using MOSAIC.Visualization;
using MOSAIC.Visualization.ScopeMonitor;
using static MOSAIC.Components.Basics.JsonModel;
using static MOSAIC.Components.Devices.Quattrocento.QuattrocentoConstants;

namespace MOSAIC.Models.Devices;

/// <summary>
/// A <see cref="BaseBlock"/> that streams multichannel EMG / EEG / AUX data from
/// an OT Bioelettronica Quattrocento amplifier over a direct TCP connection.
/// </summary>
/// <remarks>
/// <para>
/// Inputs (IN1..IN8 and MULTIPLE IN1..IN4) and AUX are toggled individually. The
/// hardware NCH preset is auto-selected as the smallest that covers all enabled
/// inputs; channels outside the selection are dropped in software.
/// All inputs and AUX default to OFF — the user opts into each.
/// </para>
/// <para>
/// Publishes <see cref="Matrix{T}"/> with rows = samples and columns =
/// (enabled bio in mV) + (optional 16 aux in V).
/// </para>
/// </remarks>
/// <example>
/// <para>Block entry for a larger pipeline. Params are key:value tokens in any order. fsamp 3 selects 10240 Hz; decim 1 enables decimation. Replace the IP address for your amplifier and select channels on the card.</para>
/// <code language="json">
/// {
///   "Quattrocento": {
///     "Type": "quattrocento",
///     "Inputs": [],
///     "Params": ["ip:169.254.1.10", "port:23456", "fsamp:3", "decim:1"]
///   }
/// }
/// </code>
/// </example>
public sealed partial class Quattrocento : BaseBlock
{
    /// <summary>Source block — produces a stream and takes no inputs.</summary>
    public override int MinInputs => 0;

    /// <inheritdoc cref="MinInputs"/>
    public override int MaxInputs => 0;

    #region Observable Properties

    [ObservableProperty] private string _ip = "169.254.1.10";
    [ObservableProperty] private int    _port = 23456;
    [ObservableProperty] private int    _fsampIndex = 3;
    [ObservableProperty] private bool   _decimatorEnabled = true;

    /// <summary>Whether the 16 AUX channels are forwarded and plotted. Default: false.</summary>
    [ObservableProperty] private bool _isAuxEnabled;

    #endregion

    #region Public Surface

    public QuattrocentoStreamInfo StreamInfo { get; } = new();
    public ScopeMonitor           Scope      { get; } = new();
    public BlockVisualization     Viz        { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>IN1..IN8 (16 ch each), toggleable. Default all off.</summary>
    public ObservableCollection<QuattrocentoInput> InsCollection { get; } = new();

    /// <summary>MULTIPLE IN1..IN4 (64 ch each), toggleable. Default all off.</summary>
    public ObservableCollection<QuattrocentoInput> MultsCollection { get; } = new();

    /// <summary>
    /// Effective NCH index — the smallest preset (0–3) whose channel grouping
    /// includes every currently enabled input.
    /// </summary>
    public int NchIndex
    {
        get
        {
            int maxIn = 0;
            for (int i = 0; i < InsCollection.Count; i++)
                if (InsCollection[i].IsEnabled) maxIn = i + 1;
            int maxMult = 0;
            for (int j = 0; j < MultsCollection.Count; j++)
                if (MultsCollection[j].IsEnabled) maxMult = j + 1;

            // NCH=N → 2(N+1) INs and (N+1) MULTs
            int reqFromIn   = maxIn   > 0 ? (maxIn + 1) / 2 - 1 : 0;
            int reqFromMult = maxMult > 0 ?  maxMult       - 1 : 0;
            return Math.Clamp(Math.Max(reqFromIn, reqFromMult), 0, 3);
        }
    }

    /// <summary>Human-readable summary for live UI feedback.</summary>
    public string EffectiveSummary
    {
        get
        {
            int activeIns   = InsCollection.Count(i => i.IsEnabled);
            int activeMults = MultsCollection.Count(m => m.IsEnabled);
            int bio         = activeIns * 16 + activeMults * 64;
            int auxOut      = IsAuxEnabled ? AuxChannelCount : 0;
            int totalOut    = bio + auxOut;

            if (totalOut == 0)
                return "Nothing selected — pick at least one IN, MULT, or AUX.";

            int totalStream = TotalChannelCounts[NchIndex];
            string auxStr   = IsAuxEnabled ? " + 16 aux" : "";
            return $"{activeIns} IN + {activeMults} MULT = {bio} bio{auxStr} → {totalOut} ch out  " +
                   $"(device streams {totalStream}, NCH={NchIndex})";
        }
    }

    /// <summary>True when at least one output channel is selected.</summary>
    public bool HasAnyOutput
        => IsAuxEnabled
        || InsCollection.Any(i => i.IsEnabled)
        || MultsCollection.Any(m => m.IsEnabled);

    #endregion

    #region Internal State

    private TcpClient?     _tcpClient;
    private NetworkStream? _stream;
    private Thread?        _readerThread;
    private volatile bool  _reading;

    private int  _totalChannels;
    private int  _streamedBioChannels;
    private int  _outputBioChannels;
    private bool _emitAux;
    private int  _bytesPerSample;
    private int  _samplesPerBatch;

    private int _presetIns;
    private int _presetMults;
    private int _auxOffsetCh;

    private ushort _lastRamp;
    private bool   _rampInitialised;

    private long     _samplesReceivedBacking;
    private long     _framesPublishedBacking;
    private long     _droppedSamplesBacking;
    private DateTime _lastVizTime = DateTime.MinValue;

    #endregion

    #region Construction & Factory

    public Quattrocento(
        string name        = "Quattrocento",
        double desiredRate = 16,
        string ip          = "169.254.1.10",
        int    port        = 23456,
        int    fsampIndex  = 3,
        bool   decimator   = true)
        : base(name, desiredRate)
    {
        _ip               = ip;
        _port             = port;
        _fsampIndex       = Math.Clamp(fsampIndex, 0, 3);
        _decimatorEnabled = decimator;

        InitializeInputs();
        RecalcDerived();
    }

    private void InitializeInputs()
    {
        InsCollection.Clear();
        for (int i = 0; i < 8; i++)
        {
            var input = new QuattrocentoInput
            {
                Label      = $"IN{i + 1}",
                Index      = i,
                IsMultiple = false,
            };
            input.PropertyChanged += OnInputChanged;
            InsCollection.Add(input);
        }

        MultsCollection.Clear();
        for (int j = 0; j < 4; j++)
        {
            var input = new QuattrocentoInput
            {
                Label      = $"MULT{j + 1}",
                Index      = j,
                IsMultiple = true,
            };
            input.PropertyChanged += OnInputChanged;
            MultsCollection.Add(input);
        }
    }

    private void OnInputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuattrocentoInput.IsEnabled)) return;
        if (StreamInfo.IsStreaming) return;     // ignore mid-stream toggles

        OnPropertyChanged(nameof(NchIndex));
        OnPropertyChanged(nameof(EffectiveSummary));
        OnPropertyChanged(nameof(HasAnyOutput));
        RecalcDerived();
    }

    public static Quattrocento ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "Quattrocento";
        var rate = m.DesiredRate ?? 16;

        var ip    = "169.254.1.10";
        var port  = 23456;
        var fsamp = 3;
        var decim = true;

        if (m.Params is { Count: > 0 })
        {
            foreach (var p in m.Params)
            {
                var raw = GetString(p)?.Trim();
                if (string.IsNullOrEmpty(raw)) continue;

                var colonIdx = raw.IndexOf(':');
                if (colonIdx < 0) continue;

                var key = raw[..colonIdx].Trim().ToLowerInvariant();
                var val = raw[(colonIdx + 1)..].Trim();

                switch (key)
                {
                    case "ip":    ip = val; break;
                    case "port":  if (int.TryParse(val, out var pv)) port  = pv; break;
                    case "fsamp": if (int.TryParse(val, out var fv)) fsamp = Math.Clamp(fv, 0, 3); break;
                    case "decim": if (int.TryParse(val, out var dv)) decim = dv != 0; break;
                    // "nch" parsed silently for backward compat — inputs are now picked in the UI.
                    case "nch": break;
                }
            }
        }

        var block = ActivatorUtilities.CreateInstance<Quattrocento>(
            sp, name, rate, ip, port, fsamp, decim);

        return block;
    }

    #endregion

    #region JSON Export

    protected override string JsonTypeName => "QuattrocentoBlock";

    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object>
        {
            $"ip:{Ip}",
            $"port:{Port}",
            $"fsamp:{FsampIndex}",
            $"decim:{(DecimatorEnabled ? 1 : 0)}",
        };

    #endregion

    #region Connect / Disconnect

    public void Connect()
    {
        if (StreamInfo.IsConnected) return;

        if (!HasAnyOutput)
        {
            StreamInfo.ConnectionStatus = "No inputs selected — enable at least one IN, MULT, or AUX.";
            Debug.WriteLine($"[{Name}] Connect aborted: no inputs selected.");
            return;
        }

        RecalcDerived();
        StreamInfo.ConnectionStatus = $"Connecting to {Ip}:{Port}…";

        Task.Run(() =>
        {
            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.Connect(Ip, Port);
                _tcpClient.NoDelay           = true;
                _tcpClient.ReceiveBufferSize = Math.Max(65536, _bytesPerSample * _samplesPerBatch * 4);
                _stream = _tcpClient.GetStream();

                int effectiveNch = NchIndex;

                var startCmd = QuattrocentoProtocol.BuildCommand(
                    acqOn: true, recOn: false,
                    decimator: DecimatorEnabled, fsamp: FsampIndex, nch: effectiveNch);
                _stream.Write(startCmd, 0, startCmd.Length);

                Thread.Sleep(500);

                var triggerCmd = QuattrocentoProtocol.BuildCommand(
                    acqOn: true, recOn: true,
                    decimator: DecimatorEnabled, fsamp: FsampIndex, nch: effectiveNch);
                _stream.Write(triggerCmd, 0, triggerCmd.Length);

                int outAux = _emitAux ? AuxChannelCount : 0;
                StreamInfo.IsConnected      = true;
                Viz.UpdateSignalRate(StreamInfo.NominalRate);
                StreamInfo.ConnectionStatus =
                    $"Connected — {_outputBioChannels} bio + {outAux} aux @ {StreamInfo.NominalRate} Hz";
                Debug.WriteLine($"[{Name}] Connected — stream {_totalChannels} ch (NCH={effectiveNch}, " +
                                $"{_streamedBioChannels} bio), out {_outputBioChannels} bio + {outAux} aux @ {StreamInfo.NominalRate} Hz");

                StartStreaming();
            }
            catch (Exception ex)
            {
                StreamInfo.ConnectionStatus = $"Failed: {ex.Message}";
                Debug.WriteLine($"[{Name}] Connect failed: {ex.Message}");
                CleanupConnection();
            }
        });
    }

    public void Disconnect()
    {
        if (!StreamInfo.IsConnected) return;
        StopStreaming();

        try
        {
            if (_stream is { CanWrite: true })
            {
                var stopCmd = QuattrocentoProtocol.BuildCommand(
                    acqOn: false, recOn: false,
                    decimator: DecimatorEnabled, fsamp: FsampIndex, nch: NchIndex);
                _stream.Write(stopCmd, 0, stopCmd.Length);
            }
        }
        catch { /* best effort */ }

        CleanupConnection();
        StreamInfo.ConnectionStatus = "Disconnected";
        Debug.WriteLine($"[{Name}] Disconnected.");
    }

    public Task ConnectAsync() => Task.Run(Connect);

    private void CleanupConnection()
    {
        try { _stream?.Close(); }    catch { }
        try { _tcpClient?.Close(); } catch { }
        _stream                = null;
        _tcpClient             = null;
        StreamInfo.IsConnected = false;
        StreamInfo.IsStreaming = false;
    }

    #endregion

    #region Streaming

    private void StartStreaming()
    {
        if (StreamInfo.IsStreaming || !StreamInfo.IsConnected) return;

        _reading                = true;
        _rampInitialised        = false;
        _lastRamp               = 0;
        _samplesReceivedBacking = 0;
        _framesPublishedBacking = 0;
        _droppedSamplesBacking  = 0;
        StreamInfo.ResetCounters();

        _readerThread = new Thread(ReaderLoop)
        {
            IsBackground = true,
            Name         = $"Quattrocento-{Name}",
            Priority     = ThreadPriority.AboveNormal,
        };
        _readerThread.Start();
        StreamInfo.IsStreaming = true;

        DesiredRate = Math.Max(1, (double)StreamInfo.NominalRate / _samplesPerBatch);

        int outCh = _outputBioChannels + (_emitAux ? AuxChannelCount : 0);
        Debug.WriteLine($"[{Name}] Streaming — batch={_samplesPerBatch}, " +
                        $"{outCh} signal ch out, emit≈{DesiredRate:F1} Hz");
    }

    private void StopStreaming()
    {
        if (!StreamInfo.IsStreaming) return;
        _reading = false;
        _readerThread?.Join(3000);
        _readerThread          = null;
        StreamInfo.IsStreaming = false;
        Debug.WriteLine($"[{Name}] Streaming stopped.");
    }

    #endregion

    #region Reader Thread

    private void ReaderLoop()
    {
        int batchBytes = _bytesPerSample * _samplesPerBatch;
        var accum      = new byte[batchBytes + 131072];
        int accumCount = 0;

        try
        {
            while (_reading && _stream is not null)
            {
                int space = accum.Length - accumCount;
                if (space <= 0) { accumCount = 0; continue; }

                int bytesRead = _stream.Read(accum, accumCount, space);
                if (bytesRead <= 0) break;
                accumCount += bytesRead;

                int consumed = 0;
                while (consumed + batchBytes <= accumCount)
                {
                    DecodeBatchAndPublish(accum, consumed);
                    consumed += batchBytes;
                }

                int remaining = accumCount - consumed;
                if (remaining > 0 && consumed > 0)
                    Buffer.BlockCopy(accum, consumed, accum, 0, remaining);
                accumCount = remaining;
            }
        }
        catch (Exception ex)
        {
            if (_reading)
                Debug.WriteLine($"[{Name}] Reader error: {ex.Message}");
        }
        finally
        {
            StreamInfo.IsStreaming = false;
            Debug.WriteLine($"[{Name}] Reader exited. " +
                            $"Samples={_samplesReceivedBacking}, Dropped={_droppedSamplesBacking}");
        }
    }

    private void DecodeBatchAndPublish(byte[] buf, int batchOffset)
    {
        int outAux   = _emitAux ? AuxChannelCount : 0;
        int outSigCh = _outputBioChannels + outAux;
        if (outSigCh == 0) return;     // nothing selected; skip

        var matrix = Matrix<double>.Build.Dense(_samplesPerBatch, outSigCh);

        for (int s = 0; s < _samplesPerBatch; s++)
        {
            int sampleOff = batchOffset + s * _bytesPerSample;
            int outCol    = 0;

            // Enabled IN inputs (16 ch each)
            for (int i = 0; i < _presetIns; i++)
            {
                if (!InsCollection[i].IsEnabled) continue;
                int devStart = i * 16;
                for (int ch = 0; ch < 16; ch++)
                {
                    short raw = BitConverter.ToInt16(buf, sampleOff + (devStart + ch) * 2);
                    matrix[s, outCol++] = raw * BioGainFactor;
                }
            }

            // Enabled MULTIPLE IN inputs (64 ch each)
            for (int j = 0; j < _presetMults; j++)
            {
                if (!MultsCollection[j].IsEnabled) continue;
                int devStart = _presetIns * 16 + j * 64;
                for (int ch = 0; ch < 64; ch++)
                {
                    short raw = BitConverter.ToInt16(buf, sampleOff + (devStart + ch) * 2);
                    matrix[s, outCol++] = raw * BioGainFactor;
                }
            }

            // AUX channels (only if enabled)
            if (_emitAux)
            {
                for (int a = 0; a < AuxChannelCount; a++)
                {
                    short raw = BitConverter.ToInt16(buf, sampleOff + (_auxOffsetCh + a) * 2);
                    matrix[s, outCol++] = raw * AuxGainFactor;
                }
            }

            // Accessory channels — internal use only
            CheckRamp(buf, sampleOff);
            ReadBufferUsage(buf, sampleOff);

            Interlocked.Increment(ref _samplesReceivedBacking);
        }

        StreamInfo.SamplesReceived = _samplesReceivedBacking;
        Interlocked.Increment(ref _framesPublishedBacking);
        StreamInfo.FramesPublished = _framesPublishedBacking;

        FeedViz(matrix);
        Publish(matrix);
    }

    /// <summary>
    /// Feeds the visualization bundle with the full batch matrix. The scope
    /// drains all rows via its batch entry point (no per-sample throttle);
    /// spider/heatmap receive only the last row. Throttled to ~60 fps.
    /// </summary>
    private void FeedViz(Matrix<double> matrix)
    {
        if (matrix.RowCount == 0 || matrix.ColumnCount == 0) return;

        var now = DateTime.UtcNow;
        if ((now - _lastVizTime).TotalMilliseconds < 16) return;
        _lastVizTime = now;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Viz.Feed(matrix);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    #endregion

    #region Ramp / Drop Detection

    private void CheckRamp(byte[] buf, int sampleOffset)
    {
        int byteIdx = sampleOffset + RampChannelIndex(_totalChannels) * 2;
        if (byteIdx + 1 >= buf.Length) return;

        ushort rampVal = (ushort)BitConverter.ToInt16(buf, byteIdx);

        if (_rampInitialised)
        {
            int gap = (ushort)(rampVal - _lastRamp);
            if (gap > 1)
            {
                long dropped = gap - 1;
                Interlocked.Add(ref _droppedSamplesBacking, dropped);
                StreamInfo.DroppedSamples = _droppedSamplesBacking;
                StreamInfo.DataLoss       = true;

                if (_droppedSamplesBacking <= 50)
                    Debug.WriteLine($"[{Name}] Drop: ramp {_lastRamp} → {rampVal} (gap {gap}, lost {dropped})");
            }
            else
            {
                StreamInfo.DataLoss = false;
            }
        }

        _lastRamp        = rampVal;
        _rampInitialised = true;
    }

    private void ReadBufferUsage(byte[] buf, int sampleOffset)
    {
        int byteIdx = sampleOffset + BufferUsageChannelIndex(_totalChannels) * 2;
        if (byteIdx + 1 >= buf.Length) return;
        StreamInfo.BufferUsage = BitConverter.ToInt16(buf, byteIdx);
    }

    #endregion

    #region Derived Calculations

    private void RecalcDerived()
    {
        int nch = NchIndex;
        _presetIns           = 2 * (nch + 1);
        _presetMults         = nch + 1;
        _totalChannels       = TotalChannelCounts[nch];
        _streamedBioChannels = BioChannelCount(_totalChannels);
        _auxOffsetCh         = _streamedBioChannels;
        _bytesPerSample      = _totalChannels * 2;

        int outBio = 0;
        for (int i = 0; i < _presetIns; i++)
            if (InsCollection[i].IsEnabled) outBio += 16;
        for (int j = 0; j < _presetMults; j++)
            if (MultsCollection[j].IsEnabled) outBio += 64;
        _outputBioChannels = outBio;
        _emitAux           = IsAuxEnabled;

        int outAux   = _emitAux ? AuxChannelCount : 0;
        int outSigCh = _outputBioChannels + outAux;

        int rate    = SampleRateValues[Math.Clamp(FsampIndex, 0, 3)];
        int desRate = Math.Max(1, (int)DesiredRate);
        _samplesPerBatch = Math.Max(1, rate / desRate);

        StreamInfo.NominalRate        = rate;
        StreamInfo.TotalChannels      = _totalChannels;
        StreamInfo.BioChannelCount    = _outputBioChannels;
        StreamInfo.SignalChannelCount = outSigCh;
        StreamInfo.SamplesPerBatch    = _samplesPerBatch;

        Debug.WriteLine($"[{Name}] Config: stream {_totalChannels} ch (NCH={nch}, " +
                        $"{_streamedBioChannels} bio), out {_outputBioChannels} bio + {outAux} aux, " +
                        $"{rate} Hz, batch={_samplesPerBatch}");
    }

    partial void OnFsampIndexChanged(int value) => RecalcDerived();

    partial void OnIsAuxEnabledChanged(bool value)
    {
        if (StreamInfo.IsStreaming) return;
        OnPropertyChanged(nameof(EffectiveSummary));
        OnPropertyChanged(nameof(HasAnyOutput));
        RecalcDerived();
    }

    #endregion

    #region OnReceive

    protected override void OnReceive(object sender, object data) { }

    #endregion

    #region Dispose

    public override void Dispose()
    {
        foreach (var i in InsCollection)   i.PropertyChanged -= OnInputChanged;
        foreach (var m in MultsCollection) m.PropertyChanged -= OnInputChanged;

        try { Scope.Pause(); Scope.Dispose(); } catch { }
        Disconnect();
        Viz.Dispose();
        base.Dispose();
    }

    #endregion
}