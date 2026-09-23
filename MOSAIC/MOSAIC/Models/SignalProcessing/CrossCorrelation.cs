using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;
using MOSAIC.Visualization.SnapshotMonitor;
using MOSAIC.Visualization.SpectrogramMonitor;

namespace MOSAIC.Models.SignalProcessing;

/// <summary>
/// Two-channel cross-correlation block, ported from feature_xcorr (819bb1b).
/// Consumes a Matrix&lt;double&gt; [rows x 2] (col0 = ch1, col1 = ch2) — or, as a
/// fallback, an interleaved Vector&lt;double&gt; (even = ch1, odd = ch2).
/// Pipeline per processed batch: z-normalize → 10-sample moving maximum →
/// subtract a 500-sample forward mean → cross-correlate. Output lags ascend from
/// -MaxLag to +MaxLag (0 selects the full range); the result has L2 norm 10,
/// matching the feature branch's display scaling, not a Pearson coefficient.
/// Params: MaxLag (0), BufferLen (1000), ProcessEveryN packets (5).
/// Named Lags parameters instead enable the newer selectable-channel/filter pipeline
/// and publish [peak lag, sample rate / lag, reserved zero]; see CrossCorrelation.Configuration.cs.
/// </summary>
/// <example>
/// <para>Block entry for a larger pipeline. Legacy Params: maximum lag (0 means full range), buffer length, processing interval in packets. Supply a multi-channel observation stream and wait for the buffer to fill. The legacy output is scaled to norm 10, not a Pearson coefficient.</para>
/// <code language="json">
/// {
///   "CrossCorrelation": {
///     "Type": "crosscorrelation",
///     "Inputs": ["Signal"],
///     "Params": [0, 1000, 5]
///   }
/// }
/// </code>
/// </example>
public sealed partial class CrossCorrelation : BaseBlock
{
    // Below this std the channel is effectively constant and z-normalize would
    // silently emit zeros — we surface it instead.
    private const double FlatStdThreshold = 1e-6;
    private const int EnvelopeWindow = 10;
    private const int LegacyHighPassWindow = 500;
    public const int MinimumBufferLength = EnvelopeWindow + LegacyHighPassWindow + 1;

    private readonly List<double> _bufCh1 = new();
    private readonly List<double> _bufCh2 = new();
    private int _counter;
    private bool _resourcesDisposed;

    // Keep concurrent producers and disposal from mutating the buffers together.
    // This lock keeps buffer append, compute, and trim atomic — without
    // it, a concurrent Add/RemoveRange corrupts the DenseOfEnumerable copy
    // ("Destination array was not long enough").
    private readonly object _bufferLock = new();

    // Optional CSV dumpers for the two normalized channel curves shown on the card.
    // The base Dumper already logs the published cross-correlation result.
    private CsvDumper? _ch1Dumper;
    private CsvDumper? _ch2Dumper;

    // Static line-plots (each EnqueueSnapshot redraws the whole array, no scrolling).
    public SnapshotMonitor SnapXCorr { get; } = new();
    public SnapshotMonitor SnapCh1 { get; } = new();
    public SnapshotMonitor SnapCh2 { get; } = new();

    public SpectrogramMonitor CorrelogramOverTime { get; }

    // Correlogram: each cross-correlation vector is fed as one heatmap frame, so
    // the built-in HeatMap shows lag (vertical) × time (horizontal), colour = value.
    public BlockVisualization Viz { get; } = new();
    protected override BlockVisualization? Visualization => Viz;
    protected override string DumpFilePrefix => $"{Name}_xcorr";

    [ObservableProperty] private int _maxLag;
    [ObservableProperty] private int _bufferLen;
    [ObservableProperty] private int _processEveryN;
    [ObservableProperty] private long _updatesComputed;

    // ── Live input-health diagnostics (so a flat/dead channel is never silent) ──
    [ObservableProperty] private double _ch1Std;
    [ObservableProperty] private double _ch2Std;
    [ObservableProperty] private bool _inputFlat;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public CrossCorrelation(string name, double desiredRate,
        int maxLag, int bufferLen, int processEveryN)
        : base(name, desiredRate)
    {
        _maxLag = Math.Max(0, maxLag);
        _bufferLen = Math.Max(MinimumBufferLength, bufferLen);
        _processEveryN = Math.Max(1, processEveryN);
        CorrelogramOverTime = new SpectrogramMonitor(200);
    }

    partial void OnMaxLagChanged(int value)
    {
        if (UsesLagSummary)
        {
            if (value < MinLag || value > 100000) MaxLag = Math.Clamp(value, MinLag, 100000);
            if (BufferLen < RequiredSamples) BufferLen = RequiredSamples;
        }
        else if (value < 0) MaxLag = 0;
    }

    partial void OnBufferLenChanged(int value)
    {
        if (value < RequiredSamples) BufferLen = RequiredSamples;
    }

    partial void OnProcessEveryNChanged(int value)
    {
        if (value < 1) ProcessEveryN = 1;
    }

    public static CrossCorrelation ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var p = m.Params;
        if (p is { Count: > 0 } && (p[0]?.ToString()?.Contains(':') == true || p.Count > 3))
            return ConfigureLagSummary(sp, m);
        var name = m.Name ?? "CrossCorr";
        var rate = m.DesiredRate ?? 0;
        int maxLag = JsonModel.GetInt(p is { Count: > 0 } ? p[0] : null, 0);
        int bufferLen = JsonModel.GetInt(p is { Count: > 1 } ? p[1] : null, 1000);
        int processEveryN = JsonModel.GetInt(p is { Count: > 2 } ? p[2] : null, 5);

        var block = ActivatorUtilities.CreateInstance<CrossCorrelation>(
            sp, name, rate, maxLag, bufferLen, processEveryN);
        // BlockFactory owns the primary dumper and recording switch in net10migration.
        block.InitChannelDumpers(sp, m.Path);
        return block;
    }

    /// <summary>Creates CSV dumpers for the two normalized channel curves, mirroring
    /// the base <see cref="BaseBlock.InitDumper"/> behaviour (only when Path is set).</summary>
    private void InitChannelDumpers(IServiceProvider sp, string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _ch1Dumper = CsvDumper.ConfigureInput(sp, path, $"{Name}_ch1");
        _ch2Dumper = CsvDumper.ConfigureInput(sp, path, $"{Name}_ch2");
    }

    protected override void OnReceive(object sender, object value)
    {
        lock (_bufferLock)
        {
            if (_resourcesDisposed) return;

            if (value is Matrix<double> mtx && mtx.ColumnCount >= ChannelCount && mtx.RowCount > 0)
            {
                for (int r = 0; r < mtx.RowCount; r++)
                {
                    _bufCh1.Add(ReadChannel(mtx, r, SelectedChannel1));
                    _bufCh2.Add(ReadChannel(mtx, r, SelectedChannel2));
                }
            }
            else if (value is Vector<double> v && v.Count >= ChannelCount && v.Count % ChannelCount == 0)
            {
                for (int i = 0; i < v.Count; i += ChannelCount)
                {
                    _bufCh1.Add(ReadChannel(v, i, SelectedChannel1));
                    _bufCh2.Add(ReadChannel(v, i, SelectedChannel2));
                }
            }
            else
            {
                StatusMessage = $"Expected {ChannelCount}-channel samples (matrix columns or interleaved vector).";
                return;
            }

            // Bound the actual computation window even if one packet exceeds BufferLen.
            TrimBuffers();
            _counter = (_counter + 1) % Math.Max(1, ProcessEveryN);
            if (_counter == 0)
                ComputeAndPublish();
        }
    }

    private void ComputeAndPublish()
    {
        if (_bufCh1.Count != _bufCh2.Count) return;
        // The original filters shorten their input by 10 and then 500 samples.
        // Do not allocate negative arrays or emit empty/degenerate warm-up frames.
        if (_bufCh1.Count < RequiredSamples)
        {
            StatusMessage = $"Warming up: {_bufCh1.Count}/{RequiredSamples} samples";
            return;
        }

        // Retain the feature branch's normalization/envelope/high-pass processing.
        var x = Vector<double>.Build.DenseOfEnumerable(_bufCh1);
        var y = Vector<double>.Build.DenseOfEnumerable(_bufCh2);

        // Input-health guard: a near-constant channel makes z-normalize emit
        // zeros (the old "everything is flat" failure). Surface it explicitly.
        double sdx = Std(x), sdy = Std(y);
        Ch1Std = sdx;
        Ch2Std = sdy;
        bool flat = sdx < FlatStdThreshold || sdy < FlatStdThreshold;
        InputFlat = flat;
        StatusMessage = flat
            ? $"Flat input (σ1={sdx:G3}, σ2={sdy:G3}) — no variation to correlate"
            : string.Empty;

        var xn = UsesLagSummary ? ProcessConfigured(x) : HighPass(MovMax(Normalize(x), EnvelopeWindow, 1), LegacyHighPassWindow, 1);
        var yn = UsesLagSummary ? ProcessConfigured(y) : HighPass(MovMax(Normalize(y), EnvelopeWindow, 1), LegacyHighPassWindow, 1);

        SnapCh1.EnqueueSnapshot(xn);
        SnapCh2.EnqueueSnapshot(yn);

        // Persist the normalized channel curves (same data shown on the card).
        double now = TickTracker.Now();
        // Explicit-Path channel exports must stop with the current recording toggle too.
        if (Dumper is not null)
        {
            _ch1Dumper?.Enqueue(now, xn);
            _ch2Dumper?.Enqueue(now, yn);
        }

        int firstLag = UsesLagSummary && !_fullLagRange ? MinLag : (MaxLag == 0 ? -yn.Count + 1 : -MaxLag);
        int lastLag = UsesLagSummary && !_fullLagRange ? MaxLag : (MaxLag == 0 ? xn.Count - 1 : MaxLag);
        var result = ComputeCrossCorrelationRange(xn, yn, firstLag, lastLag);
        if (result.Count == 0) return;

        var output = UsesLagSummary ? BuildLagSummary(result, firstLag, flat) : result;
        Publish(output);
        SnapXCorr.EnqueueSnapshot(result);
        Viz.Feed(output);
        CorrelogramOverTime.EnqueueSpectrum(result.ToArray());
        UpdatesComputed++;
    }

    private void TrimBuffers()
    {
        int excess = _bufCh1.Count - Math.Max(RequiredSamples, BufferLen);
        if (excess > 0)
        {
            _bufCh1.RemoveRange(0, excess);
            _bufCh2.RemoveRange(0, excess);
        }
    }

    private static Vector<double> MovMax(Vector<double> signal, int windowSize, int stepSize)
    {
        var result = new double[(signal.Count) - windowSize];
        int avgIndex = 0;
        Vector<double> subVector;


        for (int start = 0; start < signal.Count - windowSize; start += stepSize)
        {
            subVector = signal.SubVector(start, windowSize);
            result[avgIndex] = subVector.Max();
            avgIndex++;
        }

        return Vector<double>.Build.DenseOfArray(result);
    }

    private static Vector<double> HighPass(Vector<double> signal, int windowSize, int stepSize)
    {

        Vector<double> subVector;
        var averages = new double[(signal.Count) - windowSize];
        int avgIndex = 0;

        for (int start = 0; start < signal.Count - windowSize; start += stepSize)
        {
            subVector = signal.SubVector(start, windowSize);

            averages[avgIndex] = signal.ElementAt(start) - subVector.Sum() / windowSize;
            avgIndex++;
        }

        return Vector<double>.Build.DenseOfArray(averages);
    }

    // ── DSP helpers ──

    private static Vector<double> Normalize(Vector<double> signal)
    {
        double mean = signal.Average();
        double std = Math.Sqrt(signal.Select(v => (v - mean) * (v - mean)).Average());
        if (std < 1e-9) return Vector<double>.Build.Dense(signal.Count, 0.0);
        return signal.Map(v => (v - mean) / std);
    }

    private static double Std(Vector<double> s)
    {
        if (s.Count == 0) return 0.0;
        double mean = s.Average();
        return Math.Sqrt(s.Select(v => (v - mean) * (v - mean)).Average());
    }

    internal static Vector<double> ComputeCrossCorrelation(Vector<double> x, Vector<double> y, int maxLag)
        => ComputeCrossCorrelationRange(x, y, maxLag == 0 ? -y.Count + 1 : -maxLag,
            maxLag == 0 ? x.Count - 1 : maxLag);

    internal static Vector<double> ComputeCrossCorrelationRange(Vector<double> x, Vector<double> y, int minLag, int maxLag)
    {
        int n = x.Count, m = y.Count;
        if (n == 0 || m == 0) return Vector<double>.Build.Dense(0);
        int min = minLag, max = checked(maxLag + 1), length = checked(maxLag - minLag + 1);

        var result = Vector<double>.Build.Dense(length);
        for (int lag = min; lag < max; lag++)
        {
            double sum = 0.0;
            for (int i = Math.Max(0, lag); i < Math.Min(n, m + lag); i++)
            {
                int j = i - lag;
                sum += x[i] * y[j];
            }
            result[lag - min] = sum;
        }

        double norm = result.L2Norm();
        return norm > 1e-12 ? result / norm * 10.0 : result;   // ×10 for display
    }

    protected override string JsonTypeName => "CrossCorrelation";

    protected override IReadOnlyList<object>? GetJsonParams()
        => UsesLagSummary ? GetLagSummaryParams() : new List<object> { MaxLag, BufferLen, ProcessEveryN };

    protected override void Dispose(bool disposing)
    {
        lock (_bufferLock)
        {
            if (_resourcesDisposed) return;
            _resourcesDisposed = true;
            if (disposing)
            {
                SnapXCorr.Dispose();
                SnapCh1.Dispose();
                SnapCh2.Dispose();
                CorrelogramOverTime.Dispose();
                Viz.Dispose();
                if (_ch1Dumper is not null) TrackCleanup(_ch1Dumper.DisposeAsync().AsTask());
                if (_ch2Dumper is not null) TrackCleanup(_ch2Dumper.DisposeAsync().AsTask());
                _bufCh1.Clear();
                _bufCh2.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
