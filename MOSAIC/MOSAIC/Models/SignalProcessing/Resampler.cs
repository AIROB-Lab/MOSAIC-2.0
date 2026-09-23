using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;

namespace MOSAIC.Models.SignalProcessing;

/// <summary>
/// Sample-time resampler with zero-order hold and stable output snapshots.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> Converts the input sample spacing to <see cref="TargetRate"/> using
/// zero-order hold. Output values are stable snapshots. Sample time is inferred from upstream
/// metadata (vector publication rate or matrix row rate), with the constructor input rate as a
/// fallback. Without input metadata, the target rate is assumed. Output arrives on input callbacks,
/// so use a separate clocked sink if wall-clock delivery must be paced. Apply an appropriate
/// low-pass filter before downsampling when aliasing matters.
/// </para>
/// <para>
/// <b>Input types:</b>
/// <list type="bullet">
///   <item><description><see cref="Vector{T}"/> — single sample, one value per channel.</description></item>
///   <item><description><see cref="Matrix{T}"/> [timesteps × channels] — each row is processed
///   as a new sample through the same timing logic. Outputs between input instants hold the
///   preceding value and are emitted when the next input arrives.</description></item>
///   <item><description><c>ValueTuple&lt;string, object&gt;</c> — legacy tagged format; inner
///   object is unwrapped and handled as above.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Rate propagation:</b> The block sets <see cref="BaseBlock.DesiredRate"/> to
/// <see cref="TargetRate"/>, ensuring all downstream blocks inherit the resampled rate.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "Downsample": {
///     "Type": "Resampler",
///     "Inputs": [ "EMG" ],
///     "Params": [ "100" ]
///   }
/// }
/// </code>
/// </example>
public sealed partial class Resampler : BaseBlock
{
    #region Public properties

    /// <summary>Visualization helper for binding the resampled output to the UI scope.</summary>
    public BlockVisualization? Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>
    /// Target output rate in Hz. Also updates <see cref="BaseBlock.DesiredRate"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResampleRatio))]
    [NotifyPropertyChangedFor(nameof(ConfigSummary))]
    private double _targetRate;

    /// <summary>Input sample rate in Hz, resolved from source metadata or the configured fallback.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResampleRatio))]
    [NotifyPropertyChangedFor(nameof(ConfigSummary))]
    private double _inputRate;

    /// <summary>Number of channels being resampled.</summary>
    [ObservableProperty] private int _channelCount;

    /// <summary>Total samples received from upstream.</summary>
    [ObservableProperty] private long _samplesReceived;

    /// <summary>Total samples published downstream.</summary>
    [ObservableProperty] private long _samplesPublished;

    /// <summary>Ratio of output rate to input rate.</summary>
    public double ResampleRatio => InputRate > 0 ? TargetRate / InputRate : 0;

    /// <summary>Human-readable summary of the resampling configuration.</summary>
    public string ConfigSummary => $"{InputRate:F0} → {TargetRate:F0} Hz";

    #endregion

    #region Private state

    private readonly object _gate = new();
    private readonly double _configuredInputRate;
    private double _nextInputTime;
    private double _nextOutputTime;
    private Vector<double>? _previous;
    protected override bool TransformsPublicationRate => true;
    protected override bool TransformsSignalRate => true;

    #endregion

    #region Construction

    public Resampler(string name, double desiredRate, double targetRate)
        : base(name, desiredRate)
    {
        if (!double.IsFinite(targetRate) || targetRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRate), "Target rate must be positive.");

        _targetRate = targetRate;
        _configuredInputRate = desiredRate;
        DesiredRate = targetRate;
        SignalRate = targetRate;
    }

    public Resampler(string name, double desiredRate)
        : this(name, desiredRate, desiredRate) { }

    public Resampler()
        : this("Resampler", 100, 100) { }

    #endregion

    #region Factory

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_resampled.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_resampled";

    public static Resampler ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name       = m.Name ?? "Resampler";
        var desiredRate = m.DesiredRate ?? 0;
        var targetRate  = ParseTargetRate(m.Params) ?? m.DesiredRate ?? 100;

        var block = new Resampler(name, desiredRate, targetRate);
        return block;
    }

    #endregion

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "Resampler";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams() => [TargetRate];

    #endregion

    #region Observable property callbacks

    partial void OnTargetRateChanging(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Target rate must be finite and positive.");
    }

    partial void OnTargetRateChanged(double value)
    {
        lock (_gate)
        {
            _nextOutputTime = _nextInputTime;
            UpdateAndPropagateRate(value);
            UpdateAndPropagateSignalRate(value);
        }
    }

    #endregion

    #region Data pipeline

    /// <summary>
    /// Resamples in sample time using zero-order hold. Packet boundaries and CPU speed do not
    /// affect the result. Intermediate output times are delivered when the next input arrives;
    /// this block does not provide an independent real-time clock or an anti-aliasing filter.
    /// </summary>
    protected override void OnReceive(object sender, object data)
    {
        if (data is ValueTuple<string, object> tagged) data = tagged.Item2;
        if (data is not Vector<double> && data is not Matrix<double>) return;
        lock (_gate)
        {
            var source = sender as BaseBlock;
            // Each vector is one sample, even if it describes features of a faster signal.
            var rate = source != this && data is Matrix<double> && source?.SignalRate > 0
                ? source.SignalRate : source != this && source?.DesiredRate > 0 ? source.DesiredRate : 0;
            if (rate <= 0) rate = _configuredInputRate > 0 ? _configuredInputRate : TargetRate;
            InputRate = rate;
            var targetRate = TargetRate;
            if (data is Vector<double> vector) ProcessSample(vector, rate, targetRate);
            else if (data is Matrix<double> matrix)
                for (int row = 0; row < matrix.RowCount; row++)
                    ProcessSample(matrix.Row(row), rate, targetRate);
        }
    }

    private void ProcessSample(Vector<double> sample, double inputRate, double targetRate)
    {
        if (_previous is not null && _previous.Count != sample.Count)
        {
            _previous = null;
            _nextOutputTime = _nextInputTime;
        }
        ChannelCount = sample.Count;
        SamplesReceived++;
        while (_nextOutputTime <= _nextInputTime + 1e-9)
        {
            var held = _previous is not null && _nextOutputTime < _nextInputTime - 1e-9
                ? _previous : sample;
            var output = held.Clone();
            Publish(output);
            if (Viz?.IsVisible == true) Viz.Feed(output);
            SamplesPublished++;
            _nextOutputTime += 1.0 / targetRate;
        }
        _previous = sample.Clone();
        _nextInputTime += 1.0 / inputRate;
    }

    #endregion

    #region Helpers

    public void ResetStatistics()
    {
        SamplesReceived  = 0;
        SamplesPublished = 0;

    }

    private static double? ParseTargetRate(IReadOnlyList<object>? @params)
    {
        if (@params is null || @params.Count == 0)
            return null;

        foreach (var param in @params)
        {
            var s = param?.ToString()?.Trim();
            if (string.IsNullOrEmpty(s)) continue;

            if (s.StartsWith("targetRate:", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("rate:",       StringComparison.OrdinalIgnoreCase))
            {
                var parts = s.Split(':', 2);
                if (parts.Length == 2 &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
                    v > 0)
                    return v;
            }

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) && val > 0)
                return val;
        }

        return null;
    }

    #endregion

    #region Disposal

    public override void Dispose()
    {
        base.Dispose();
        Viz?.Dispose();
    }

    #endregion
}
