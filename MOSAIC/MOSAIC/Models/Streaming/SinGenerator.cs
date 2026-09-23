using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;
using static MOSAIC.Components.Basics.JsonModel;

namespace MOSAIC.Models.Streaming;

/// <summary>
/// Synthetic signal source that generates <c>d</c> sinusoidal components with configurable
/// amplitude, frequency, and per-channel phase offsets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> On each tick from an upstream <see cref="MOSAIC.Models.FlowControl.ClockBlock"/>,
/// the generator evaluates <c>A × sin(2π·f·t + φ₀ + i·Δφ)</c> for each component <c>i</c>,
/// where <c>t</c> is derived from a jitter-free sample counter, <c>A</c> = <see cref="Amplitude"/>,
/// <c>f</c> = <see cref="Frequency"/>, <c>φ₀</c> = <see cref="Phase"/>,
/// and <c>Δφ</c> = <see cref="PhaseStep"/>.
/// </para>
/// <para>
/// <b>Time base:</b> Rather than relying on wall-clock timestamps (which suffer from jitter
/// and can alias high-frequency signals), time is computed as <c>sampleIndex / sampleRate</c>
/// using <see cref="BaseBlock.DesiredRate"/> (falls back to 1000 Hz if unset).
/// </para>
/// <para>
/// <b>Source block:</b> This block is driven by a clock tick (double timestamp) on
/// <see cref="BaseBlock.OnReceive"/>, not by upstream data.
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs the generated signal vector via <see cref="BaseBlock.Publish"/>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "SinWave": {
///     "Type": "SinGenerator",
///     "Inputs": [ "Clock" ],
///     "DesiredRate": 200,
///     "Params": [ 8, 1.0, 0.2, 0.0, 10.0 ],
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description><c>ComponentCount</c> (int, default 1) — number of sine channels.</description></item>
///   <item><term>1</term><description><c>Amplitude</c> (double, default 1.0).</description></item>
///   <item><term>2</term><description><c>Frequency</c> (double, default 0.2) — Hz.</description></item>
///   <item><term>3</term><description><c>Phase</c> (double, default 0.0) — base phase offset in radians.</description></item>
///   <item><term>4</term><description><c>PhaseStep</c> (double, default 10.0) — inter-channel phase step in radians.</description></item>
/// </list>
/// </para>
/// </example>
public partial class SinGenerator : BaseBlock
{
    private readonly object _settingsLock = new();
    protected override bool TransformsSignalRate => true;
    /// <summary>Visualization scope for the generated signal.</summary>
    public BlockVisualization Viz { get; set; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>Number of sine components (channels).</summary>
    private int _componentCount;
    public int ComponentCount
    {
        get { lock (_settingsLock) return _componentCount; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            lock (_settingsLock)
                if (SetProperty(ref _componentCount, value)) { OnComponentCountChanged(value); }
        }
    }

    /// <summary>Amplitude of the sine waves.</summary>
    private double _amplitude;
    public double Amplitude
    {
        get { lock (_settingsLock) return _amplitude; }
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_settingsLock)
                SetProperty(ref _amplitude, value);
        }
    }

    /// <summary>Frequency in Hz.</summary>
    private double _frequency;
    public double Frequency
    {
        get { lock (_settingsLock) return _frequency; }
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_settingsLock)
                if (SetProperty(ref _frequency, value)) { OnFrequencyChanged(value); }
        }
    }

    /// <summary>Base phase offset in radians (applied to the first component).</summary>
    private double _phase;
    public double Phase
    {
        get { lock (_settingsLock) return _phase; }
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_settingsLock)
                SetProperty(ref _phase, value);
        }
    }

    /// <summary>Phase step between consecutive components in radians.</summary>
    private double _phaseStep = 10.0;
    public double PhaseStep
    {
        get { lock (_settingsLock) return _phaseStep; }
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_settingsLock)
                SetProperty(ref _phaseStep, value);
        }
    }

    /// <summary>
    /// Samples generated per tick. One (the default) publishes a <see cref="Vector{T}"/> as before;
    /// more publishes a <see cref="Matrix{T}"/> of rows = samples, columns = components.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Above one this block emits the same shape a packetised device does — for example
    /// <c>MccDaq</c> with its <c>ScansPerPacket</c> — at a true sample rate of
    /// <c>tickRate × ScansPerPacket</c> while still publishing once per tick. That path needed a rig
    /// to exercise before, so it could not be checked on a desk.
    /// </para>
    /// <para>
    /// The phase runs continuously across packet boundaries, so a dropped, duplicated or reordered
    /// packet appears as a kink in an otherwise clean sine. Set <see cref="Frequency"/> to a whole
    /// number of hertz and count cycles against the scope's time axis: ten seconds of a 1 Hz sine is
    /// ten cycles, and any other count means the axis is wrong by exactly that ratio.
    /// </para>
    /// </remarks>
    private int _scansPerPacket = 1;
    public int ScansPerPacket
    {
        get { lock (_settingsLock) return _scansPerPacket; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            lock (_settingsLock)
                if (SetProperty(ref _scansPerPacket, value)) { OnScansPerPacketChanged(value); }
        }
    }

    /// <summary>Reusable buffer for the most recently generated signal.</summary>
    private Vector<double> _signal;

    /// <summary>Precomputed angular frequency (2π·f).</summary>
    private double _omega;

    /// <summary>Sample counter for jitter-free phase accumulation.</summary>
    private long _sampleIndex;

    private double _dt;

    public override string[] AllowableBlocks => ["ClockBlock"];

    /// <summary>
    /// True sample rate of the generated signal: one tick yields <see cref="ScansPerPacket"/> samples.
    /// </summary>
    /// <remarks>
    /// Distinct from the tick rate whenever a packet carries more than one sample, and it is this
    /// rate — not the tick rate — that the time base and the scope both need.
    /// </remarks>
    public double SampleRate =>
        (DesiredRate > 0 ? DesiredRate : InputRate) * Math.Max(1, ScansPerPacket);

    /// <summary>
    /// Initializes a new <see cref="SinGenerator"/> block.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="desiredRate">Tick rate in Hz (typically inherited from a clock).</param>
    /// <param name="componentCount">Number of sine channels. Must be ≥ 1.</param>
    /// <param name="amplitude">Signal amplitude.</param>
    /// <param name="frequency">Signal frequency in Hz.</param>
    /// <param name="phase">Base phase offset in radians.</param>
    /// <param name="phaseStep">Inter-channel phase step in radians.</param>
    public SinGenerator(
        string name = "Sin",
        double desiredRate = 0,
        int componentCount = 1,
        double amplitude = 1.0,
        double frequency = 0.2,
        double phase = 0.0,
        double phaseStep = 10.0,
        int scansPerPacket = 1)
        : base(name, desiredRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(componentCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scansPerPacket);

        _componentCount = componentCount;
        _amplitude = amplitude;
        _frequency = frequency;
        _phase = phase;
        _phaseStep = phaseStep;
        _scansPerPacket = scansPerPacket;
        _signal = Vector<double>.Build.Dense(componentCount);
        _omega = 2 * Math.PI * _frequency;

        RefreshTimeBase();
    }

    /// <summary>Resizes the signal buffer when component count changes.</summary>
    private void OnComponentCountChanged(int value)
    {
        if (value <= 0) return;
        _signal = Vector<double>.Build.Dense(value);
    }

    /// <summary>Re-derives the time base when the packet size changes at runtime.</summary>
    private void OnScansPerPacketChanged(int value)
    {
        if (value <= 0) return;
        RefreshTimeBase();
    }

    /// <inheritdoc />
    protected override void OnDesiredRateChanged(double oldRate, double newRate)
    {
        base.OnDesiredRateChanged(oldRate, newRate);
        RefreshTimeBase();
    }

    /// <inheritdoc />
    protected override void OnInputRateChanged(double newInputRate) => RefreshTimeBase();

    /// <summary>
    /// Republishes the sample rate to the sample spacing, the block graph and the local scope.
    /// </summary>
    /// <remarks>
    /// The scope has to be told separately: <see cref="BaseBlock.SignalRate"/> travels along the
    /// block graph for downstream DSP, but nothing carries it into the local visualization. And it
    /// must be given the <em>sample</em> rate rather than the tick rate, because the scope advances
    /// its x-axis once per row written and <see cref="OnReceive"/> writes every row of the packet.
    /// </remarks>
    private void RefreshTimeBase()
    {
        lock (_settingsLock)
        {
            double sampleRate = SampleRate;
            if (sampleRate <= 0) return;

            _dt = 1.0 / sampleRate;
            SignalRate = sampleRate;
            Viz?.UpdateSignalRate(sampleRate);
            OnPropertyChanged(nameof(SampleRate));
        }
    }

    /// <summary>Recomputes angular frequency when frequency changes.</summary>
    private void OnFrequencyChanged(double value)
    {
        _omega = 2 * Math.PI * value;
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_sin.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_sin";

    /// <summary>
    /// Creates a <see cref="SinGenerator"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// JSON model. See the class-level example for <c>Params</c> layout.
    /// </param>
    /// <returns>A configured <see cref="SinGenerator"/> instance.</returns>
    public static SinGenerator ConfigureInput(IServiceProvider sp, JsonModel m) 
    {
        var p = m.Params ?? new List<object>(0);

        var compCnt   = p.Count > 0 ? Math.Max(1, GetInt(p[0], 1))  : 1;
        var amplitude = p.Count > 1 ? GetDouble(p[1], 1.0)          : 1.0;
        var frequency = p.Count > 2 ? GetDouble(p[2], 0.2)          : 0.2;
        var phase     = p.Count > 3 ? GetDouble(p[3], 0.0)          : 0.0;
        var phaseStep = p.Count > 4 ? GetDouble(p[4], 10.0)         : 10.0;
        // Appended, so the five-param configs already in Assets/Examples keep loading unchanged
        // and default to one sample per tick — exactly what they did before.
        var scans     = p.Count > 5 ? Math.Max(1, GetInt(p[5], 1))  : 1;

        var name = m.Name ?? "Sin";
        var rate = m.DesiredRate ?? 0;

        var block = ActivatorUtilities.CreateInstance<SinGenerator>(
            sp, name, rate, compCnt, amplitude, frequency, phase, phaseStep, scans);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "SinGenerator";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
        => [ComponentCount, Amplitude, Frequency, Phase, PhaseStep, ScansPerPacket];

    #endregion

    /// <summary>
    /// Evaluates all sine components at the current sample index and publishes the result.
    /// </summary>
    /// <remarks>
    /// Time is computed as <c>sampleIndex / sampleRate</c> rather than from the upstream
    /// timestamp to eliminate wall-clock jitter that would alias high-frequency signals.
    /// </remarks>
    /// <param name="sender">The upstream clock block.</param>
    /// <param name="tNow">Current time in seconds (double) — unused, sample counter provides the time base.</param>
    protected override void OnReceive(object sender, object tNow)
    {
        lock (_settingsLock)
        {
            if (_dt <= 0) RefreshTimeBase();
            if (_dt <= 0) return;

            int rows = Math.Max(1, ScansPerPacket);
            int channels = ComponentCount;

            if (rows == 1)
            {
                var angleBase = _omega * (_sampleIndex * _dt) + Phase;
                for (int i = 0; i < channels; i++)
                    _signal[i] = Amplitude * Math.Sin(angleBase + PhaseStep * i);

                _sampleIndex++;

                var snapshot = (Vector<double>)_signal.Clone();
                Publish(snapshot);
                Viz?.Feed(snapshot);
                return;
            }

            // Rows are spaced one sample period apart, not one tick — that spacing is exactly what the
            // scope has to reproduce, so generating it any other way here would mask the very thing
            // this block is used to check.
            var packet = Matrix<double>.Build.Dense(rows, channels);

            for (int r = 0; r < rows; r++)
            {
                var angleBase = _omega * ((_sampleIndex + r) * _dt) + Phase;
                for (int i = 0; i < channels; i++)
                    packet[r, i] = Amplitude * Math.Sin(angleBase + PhaseStep * i);
            }

            // Advancing by the whole packet keeps the phase continuous across tick boundaries, so a
            // discontinuity in the plotted trace can only have come from the plotting path.
            _sampleIndex += rows;

            Publish(packet);
            Viz?.Feed(packet);
        }
    }
    /// <summary>
    /// Releases visualization and base class resources (including CSV dumper if configured).
    /// </summary>
    public override void Dispose()
    {
        base.Dispose();
        Viz?.Dispose();
    }
}
