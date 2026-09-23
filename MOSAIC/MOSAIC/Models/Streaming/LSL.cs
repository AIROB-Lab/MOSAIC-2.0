using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;
using LSL;

namespace MOSAIC.Models.Streaming;

/// <summary>
/// Operating mode for <see cref="LSL"/>.
/// </summary>
public enum LslMode
{
    /// <summary>Push pipeline data out to an LSL stream.</summary>
    Outlet,

    /// <summary>Pull data from an LSL stream into the pipeline.</summary>
    Inlet
}

/// <summary>
/// Unified Lab Streaming Layer block that can operate as either an LSL outlet
/// (pushing pipeline data to the network) or an LSL inlet (pulling data from
/// the network into the pipeline).
/// </summary>
/// <remarks>
/// <para>
/// <b>Outlet mode:</b> Accepts <see cref="Vector{Double}"/>, <see cref="Matrix{Double}"/>,
/// and tagged tuples from upstream. Each sample row is pushed via <c>push_sample</c>.
/// The block re-publishes downstream so it can sit mid-pipeline.
/// The outlet is created lazily on first sample if channel count is 0.
/// </para>
/// <para>
/// <b>Inlet mode:</b> Source block with no upstream inputs. Call <see cref="Start"/> to
/// spawn a background reader thread that resolves the named stream and pulls chunks.
/// Each row is published as <see cref="Vector{Double}"/>. Call <see cref="Stop"/> to shut down.
/// </para>
/// <para>
/// <b>JSON config — Outlet:</b>
/// <code>
/// "lsl_out": {
///   "Type": "LslBlock",
///   "Inputs": ["RMS"],
///   "Params": ["outlet", "MOSAIC_EMG", "EMG", 2000, 8]
/// }
/// </code>
/// </para>
/// <para>
/// <b>JSON config — Inlet:</b>
/// <code>
/// "lsl_in": {
///   "Type": "LslBlock",
///   "DesiredRate": 2000,
///   "Params": ["inlet", "BioAmp", "EMG", 5.0, 32]
/// }
/// </code>
/// Param layout: mode, stream name, stream type, rate-or-timeout, channel-count-or-chunk-len.
/// </para>
/// <para>
/// <b>Integration note:</b> Uses the source-file-based liblsl-Csharp binding
/// (<c>LSL.cs</c> + native <c>lsl.dll</c>), not a NuGet package.
/// </para>
/// </remarks>
public sealed partial class LSL : BaseBlock
{
    #region Fields — Shared

    private string _streamName;
    private string _streamType;
    private readonly LslMode _mode;

    #endregion

    #region Fields — Outlet

    private StreamOutlet? _outlet;
    private StreamInfo? _outletInfo;
    private double _nominalRate;
    private int _channelCount;
    private readonly object _initLock = new();

    #endregion

    #region Fields — Inlet

    private double _resolveTimeout;
    private int _maxChunkLen;
    private volatile bool _running;
    private Thread? _readerThread;

    #endregion

    #region Observable Properties

    /// <summary>Gets the configured mode (Inlet or Outlet).</summary>
    public LslMode Mode => _mode;

    /// <summary>Gets the LSL stream name.</summary>
    public string StreamName => _streamName;

    /// <summary>Gets the LSL stream type.</summary>
    public string StreamType => _streamType;

    /// <summary>Gets the nominal sample rate (outlet mode).</summary>
    public double NominalRate => _nominalRate;

    /// <summary>Gets the resolve timeout in seconds (inlet mode).</summary>
    public double ResolveTimeout => _resolveTimeout;

    /// <summary>Gets the max chunk length (inlet mode).</summary>
    public int MaxChunkLen => _maxChunkLen;

    /// <summary>Whether the outlet has been created or the inlet reader is active.</summary>
    [ObservableProperty] 
    private bool _isActive;

    /// <summary>Resolved or configured channel count.</summary>
    [ObservableProperty] 
    private int _resolvedChannels;

    /// <summary>Human-readable status message for the UI.</summary>
    [ObservableProperty] 
    private string _lslStatus = "Idle";
    
    /// <summary>Resolved or configured sample rate (Hz).</summary>
    [ObservableProperty] 
    private double _resolvedRate;
    
    [ObservableProperty] 
    private long _sampleCount;

    #endregion



    #region Public Surface

    /// <summary>
    /// Optional visualization bundle. Set by the ViewModel after construction.
    /// </summary>
    public BlockVisualization? Viz { get; set; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>Whether the block is currently running (inlet reader active).</summary>
    public bool IsRunning => _running;

    #endregion

    #region Construction / Factory

    /// <summary>
    /// Initialises a new <see cref="LSL"/>.
    /// </summary>
    public LSL(
        string name,
        double desiredRate,
        LslMode mode,
        string streamName,
        string streamType,
        double rateOrTimeout,
        int channelsOrChunkLen)
        : base(name, desiredRate)
    {
        _mode       = mode;
        _streamName = streamName;
        _streamType = streamType;

        if (_mode == LslMode.Outlet)
        {
            _nominalRate    = rateOrTimeout;
            _channelCount   = channelsOrChunkLen;
            _resolveTimeout = 0;
            _maxChunkLen    = 0;

            if (_channelCount > 0)
            {
                OpenOutlet(_channelCount);
                ResolvedChannels = _channelCount;
            }
        }
        else
        {
            _resolveTimeout = rateOrTimeout;
            _maxChunkLen    = channelsOrChunkLen > 0 ? channelsOrChunkLen : 64;
            _nominalRate    = 0;
            _channelCount   = 0;
        }
    }

    /// <summary>
    /// Factory method for JSON-driven pipeline construction.
    /// </summary>
    /// <param name="sp">Service provider (dependency injection).</param>
    /// <param name="m">JSON model containing Name, DesiredRate, and Params.</param>
    /// <returns>A fully configured <see cref="LSL"/> instance.</returns>
    /// <remarks>
    /// Expected <c>Params</c> layout:
    /// <list type="number">
    ///   <item><description>[0] Mode: "inlet" or "outlet" (string, default "outlet")</description></item>
    ///   <item><description>[1] Stream name (string, default "MOSAIC")</description></item>
    ///   <item><description>[2] Stream type (string, default "EMG")</description></item>
    ///   <item><description>[3] Outlet: nominal rate (Hz) / Inlet: resolve timeout (s) (double)</description></item>
    ///   <item><description>[4] Outlet: channel count / Inlet: max chunk length (int)</description></item>
    /// </list>
    /// </remarks>
    public static LSL ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var p = m.Params;

        var modeStr = p is { Count: > 0 } ? JsonModel.GetString(p[0], "outlet")! : "outlet";
        var mode = modeStr.Equals("inlet", StringComparison.OrdinalIgnoreCase)
            ? LslMode.Inlet
            : LslMode.Outlet;

        var block = new LSL(
            m.Name ?? (mode == LslMode.Inlet ? "LslInlet" : "LslOutlet"),
            m.DesiredRate ?? 0,
            mode,
            p is { Count: > 1 } ? JsonModel.GetString(p[1], "MOSAIC")! : "MOSAIC",
            p is { Count: > 2 } ? JsonModel.GetString(p[2], "EMG")!    : "EMG",
            p is { Count: > 3 } ? JsonModel.GetDouble(p[3], mode == LslMode.Outlet ? 2000.0 : 5.0)
                                : (mode == LslMode.Outlet ? 2000.0 : 5.0),
            p is { Count: > 4 } ? JsonModel.GetInt(p[4], mode == LslMode.Outlet ? 0 : 64)
                                : (mode == LslMode.Outlet ? 0 : 64));

        return block;
    }

    #endregion

    #region Runtime Settings

    /// <summary>
    /// Applies updated settings from the UI. Only effective when the block is not active.
    /// </summary>
    /// <param name="streamName">New stream name.</param>
    /// <param name="streamType">New stream type.</param>
    /// <param name="rateOrTimeout">Outlet: nominal rate (Hz). Inlet: resolve timeout (s).</param>
    /// <param name="channelsOrChunkLen">Outlet: channel count. Inlet: max chunk length.</param>
    public void ApplySettings(string streamName, string streamType, double rateOrTimeout, int channelsOrChunkLen)
    {
        _streamName = streamName;
        _streamType = streamType;

        if (_mode == LslMode.Outlet)
        {
            // Tear down existing outlet — it will be recreated lazily on next sample
            lock (_initLock)
            {
                _outlet = null;
                _outletInfo = null;
            }

            _nominalRate  = rateOrTimeout;
            _channelCount = channelsOrChunkLen;
            ResolvedChannels = _channelCount;
            ResolvedRate = _nominalRate;
            IsActive = false;
        }
        else
        {
            // Inlet: only apply when stopped
            if (_running) return;
            _resolveTimeout = rateOrTimeout;
            _maxChunkLen    = channelsOrChunkLen > 0 ? channelsOrChunkLen : 64;
        }

        LslStatus = "Settings applied";
        Debug.WriteLine($"[LslBlock] Settings updated: '{_streamName}' ({_streamType}) rate={rateOrTimeout}");
    }

    #endregion

    #region Pipeline Core

    /// <inheritdoc/>
    protected override void OnReceive(object sender, object data)
    {
        if (_mode != LslMode.Outlet) return;

        switch (data)
        {
            case Vector<double> v:
                PushVector(v);
                Viz?.Feed(v);
                break;

            case Matrix<double> m:
                for (int r = 0; r < m.RowCount; r++)
                    PushVector(m.Row(r));
                if (m.RowCount > 0) Viz?.Feed(m.Row(m.RowCount - 1));
                break;

            case ValueTuple<string, Vector<double>> tagged:
                PushVector(tagged.Item2);
                Viz?.Feed(tagged.Item2);
                break;

            case ValueTuple<string, Matrix<double>> taggedM:
                var mat = taggedM.Item2;
                for (int r = 0; r < mat.RowCount; r++)
                    PushVector(mat.Row(r));
                if (mat.RowCount > 0) Viz?.Feed(mat.Row(mat.RowCount - 1));
                break;
        }
    }

    #endregion

    #region Outlet Helpers

    private void PushVector(Vector<double> v)
    {
        EnsureOutlet(v.Count);
        if (_outlet == null) return;

        _outlet.push_sample(v.ToArray());
        SampleCount++;
        Publish(v);
    }

    private void EnsureOutlet(int channelCount)
    {
        if (_outlet != null) return;
        lock (_initLock)
        {
            if (_outlet != null) return;
            OpenOutlet(channelCount);
        }
    }

    private void OpenOutlet(int channelCount)
    {
        _channelCount = channelCount;
        ResolvedChannels = channelCount;
        ResolvedRate = _nominalRate; 

        _outletInfo = new StreamInfo(
            _streamName,
            _streamType,
            _channelCount,
            _nominalRate,
            global::LSL.channel_format_t.cf_double64,
            $"mosaic_{_streamName}_{Environment.MachineName}");

        _outlet = new StreamOutlet(_outletInfo);

        IsActive = true;
        LslStatus = $"Streaming {_channelCount} ch @ {_nominalRate} Hz";
        Debug.WriteLine($"[LslBlock] Outlet opened '{_streamName}' — {_channelCount} ch @ {_nominalRate} Hz");
    }

    #endregion

    #region Inlet Control

    /// <summary>
    /// Starts the background LSL reader thread. Only valid in <see cref="LslMode.Inlet"/> mode.
    /// </summary>
    public void Start()
    {
        if (_mode != LslMode.Inlet || _running) return;
        _running = true;
        IsActive = true;
        LslStatus = "Resolving...";

        _readerThread = new Thread(ReaderLoop)
        {
            Name = $"LSL_{_streamName}",
            IsBackground = true
        };
        _readerThread.Start();
    }

    /// <summary>
    /// Stops the background reader thread. Only valid in <see cref="LslMode.Inlet"/> mode.
    /// </summary>
    public void Stop()
    {
        if (_mode != LslMode.Inlet) return;
        _running = false;
        _readerThread?.Join(2000);
        _readerThread = null;

        IsActive = false;
        LslStatus = "Stopped";
    }

    #endregion

    #region Reader Thread

    private void ReaderLoop()
    {
        StreamInlet? inlet = null;

        try
        {
            // ── Resolve ──────────────────────────────────────────────────────
            var predicate = string.IsNullOrEmpty(_streamType)
                ? $"name='{_streamName}'"
                : $"name='{_streamName}' and type='{_streamType}'";

            Debug.WriteLine($"[LslBlock] Resolving '{predicate}' (timeout {_resolveTimeout}s)...");
            var results = global::LSL.LSL.resolve_stream(predicate, 1, _resolveTimeout);

            if (results.Length == 0)
            {
                Debug.WriteLine($"[LslBlock] No stream found matching '{predicate}'.");
                LslStatus = "Not found";
                IsActive = false;
                _running = false;
                return;
            }

            var info     = results[0];
            int channels = info.channel_count();
            double rate  = info.nominal_srate();

            ResolvedChannels = channels;
            ResolvedRate = rate;    
            LslStatus = $"Reading {channels} ch @ {rate:F0} Hz";
            Debug.WriteLine($"[LslBlock] Resolved '{info.name()}' — {channels} ch @ {rate} Hz");

            if (rate > 0)
            {
                UpdateAndPropagateRate(rate);
            }

            // ── Open inlet ───────────────────────────────────────────────────
            inlet = new StreamInlet(info);
            inlet.open_stream();

            // ── Pull loop ────────────────────────────────────────────────────
            var buffer     = new double[_maxChunkLen, channels];
            var timestamps = new double[_maxChunkLen];

            while (_running)
            {
                int pulled = inlet.pull_chunk(buffer, timestamps, 0.1);
                if (pulled <= 0) continue;

                for (int row = 0; row < pulled; row++)
                {
                    var vec = Vector<double>.Build.Dense(channels);
                    for (int ch = 0; ch < channels; ch++)
                        vec[ch] = buffer[row, ch];

                    Publish(vec);
                    Viz?.Feed(vec);
                    SampleCount++;
                }
            }
        }
        catch (Exception ex) when (_running)
        {
            Debug.WriteLine($"[LslBlock] Reader error: {ex.Message}");
            LslStatus = $"Error: {ex.Message}";
        }
        finally
        {
            try { inlet?.close_stream(); }
            catch { /* best effort */ }

            _running = false;
            IsActive = false;
            Debug.WriteLine($"[LslBlock] Reader thread exited.");
        }
    }

    #endregion

    #region JSON Export

    /// <inheritdoc/>
    protected override string JsonTypeName => "LslBlock";

    /// <inheritdoc/>
    protected override IReadOnlyList<object>? GetJsonParams()
    {
        var modeStr = _mode == LslMode.Inlet ? "inlet" : "outlet";
        double param3 = _mode == LslMode.Outlet ? _nominalRate : _resolveTimeout;
        int param4    = _mode == LslMode.Outlet ? _channelCount : _maxChunkLen;

        return new object[] { modeStr, _streamName, _streamType, param3, param4 };
    }

    #endregion

    #region Disposal

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_mode == LslMode.Inlet)
                Stop();

            try
            {
                _outlet = null;
                _outletInfo = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LslBlock] Dispose warning: {ex.Message}");
            }

            Viz?.Dispose();
        }

        base.Dispose(disposing);
    }

    #endregion
}