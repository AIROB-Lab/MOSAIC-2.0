using System;
using System.Collections.Generic;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using static MOSAIC.Components.Basics.JsonModel;

namespace MOSAIC.Models;

/// <summary>
/// Adaptive first-order IIR low-pass filter for heteroscedastic signals where noise
/// grows with signal magnitude.
/// </summary>
/// <remarks>
/// <para><b>Algorithm (per sample, per channel):</b></para>
/// <list type="number">
///   <item><description>
///     Finite-difference derivative: dx = (x[k] − x[k−1]) × fₛ
///   </description></item>
///   <item><description>
///     Filtered derivative: dx_filt[k] = αd × dx + (1 − αd) × dx_filt[k−1],
///     where αd = (2π·α / fₛ) / (1 + 2π·α / fₛ) and α = <see cref="Alpha"/> / 10 ∈ [0, 1].
///   </description></item>
///   <item><description>
///     Adaptive cutoff: fc = exp(<see cref="Offset"/> + <see cref="Deriviate"/> × |dx_filt[k]|
///     + <see cref="Magnitude"/> × |x[k]|).
///   </description></item>
///   <item><description>
///     Output: a = (2π·fc / fₛ) / (1 + 2π·fc / fₛ), y[k] = a × x[k] + (1 − a) × y[k−1].
///   </description></item>
/// </list>
///
/// <para><b>Inputs:</b> Accepts both <see cref="Vector{T}"/> (single sample) and
/// <see cref="Matrix{T}"/> (rows = time steps, columns = channels). State is maintained
/// across calls so each column is filtered as a continuous channel over time.</para>
///
/// <para><b>Parameters (UI-bindable):</b>
/// <see cref="Alpha"/> (0–10), <see cref="Offset"/> (−5 to 5),
/// <see cref="Magnitude"/> (−40 to 0), <see cref="Deriviate"/> (0 to 40).
/// <see cref="CutoffFrequency"/> exposes the adaptive fc of the first channel for UI readout.</para>
///
/// <para><b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs every published value via <see cref="BaseBlock.Publish"/>.</para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "AdaptiveLP": {
///     "Type": "AdaptiveFilter",
///     "Inputs": [ "EMG" ],
///     "DesiredRate": 200,
///     "Params": [ 5.0, 0.0, -20.0, 10.0 ],
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description><c>Alpha</c> (double, 0–10, default 5.0) — derivative pre-filter smoothing (UI scale).</description></item>
///   <item><term>1</term><description><c>Offset</c> (double, −5 to 5, default 0.0) — base exponent offset.</description></item>
///   <item><term>2</term><description><c>Magnitude</c> (double, −40 to 0, default −20.0) — signal-magnitude contribution.</description></item>
///   <item><term>3</term><description><c>Deriviate</c> (double, 0 to 40, default 10.0) — derivative contribution.</description></item>
/// </list>
/// </para>
/// </example>
public partial class AdaptiveFilterBlock : BaseBlock
{
    /// <summary>Sampling frequency in Hz (set from <see cref="BaseBlock.DesiredRate"/>).</summary>
    private double _samplingFrequency;

    /// <summary>Per-channel state: previous input sample.</summary>
    private Vector<double>? _prevInput;

    /// <summary>Per-channel state: previous filtered derivative.</summary>
    private Vector<double>? _prevDerivative;

    /// <summary>Per-channel state: previous output sample.</summary>
    private Vector<double>? _prevOutput;

    /// <summary>Whether the filter settings panel is expanded in the UI.</summary>
    [ObservableProperty] 
    private bool _isExpanded;

    /// <summary>Adaptive cutoff frequency of the first channel (Hz), updated each tick for UI readout.</summary>
    [ObservableProperty] 
    private double _cutoffFrequency;

    /// <summary>Derivative pre-filter smoothing (UI scale 0–10; internal 0–1).</summary>
    [ObservableProperty] 
    private double _alpha;

    /// <summary>Base exponent offset for the adaptive cutoff (−5 to 5).</summary>
    [ObservableProperty] 
    private double _offset;

    /// <summary>Signal-magnitude contribution to the adaptive cutoff exponent (−40 to 0).</summary>
    [ObservableProperty] 
    private double _magnitude;

    /// <summary>Derivative contribution to the adaptive cutoff exponent (0 to 40). Spelling preserved for XAML binding compatibility.</summary>
    [ObservableProperty] 
    private double _deriviate;
    
    /// <summary>
    /// Initializes a new <see cref="AdaptiveFilterBlock"/>.
    /// </summary>
    /// <param name="name">Display name of the block.</param>
    /// <param name="desiredRate">Sampling/processing rate in Hz.</param>
    /// <param name="alphaUi">Derivative pre-filter smoothing (UI scale 0–10, default 5.0).</param>
    /// <param name="offset">Base exponent offset (default 0.0).</param>
    /// <param name="magnitude">Signal-magnitude exponent contribution (default −10.0).</param>
    /// <param name="deriviate">Derivative exponent contribution (default 10.0).</param>
    public AdaptiveFilterBlock(
        string name = "AdaptiveFilter",
        double desiredRate = 0,
        double alphaUi = 5.0,
        double offset = 0.0,
        double magnitude = -10.0,
        double deriviate = 10.0) : base(name, desiredRate)
    {
        _samplingFrequency = desiredRate;

        Alpha = Math.Clamp(alphaUi, 0, 10);
        Offset = Math.Clamp(offset, -5, 5);
        Magnitude = Math.Clamp(magnitude, -40, 0);
        Deriviate = Math.Clamp(deriviate, 0, 40);
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_adaptive.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_adaptive";

    /// <summary>
    /// Creates an <see cref="AdaptiveFilterBlock"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// JSON model. <c>Params</c>: [alpha, offset, magnitude, derivative].
    /// See the class-level example.
    /// </param>
    /// <returns>A configured <see cref="AdaptiveFilterBlock"/> instance.</returns>
    public static AdaptiveFilterBlock ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var p = m.Params ?? new List<object>(0);

        double alphaUi   = p.Count > 0 ? GetDouble(p[0], 5.0)   : 5.0;
        double off        = p.Count > 1 ? GetDouble(p[1], 0.0)   : 0.0;
        double mag        = p.Count > 2 ? GetDouble(p[2], -20.0) : -20.0;
        double deriv      = p.Count > 3 ? GetDouble(p[3], 10.0)  : 10.0;

        var name = m.Name ?? "AdaptiveFilter";
        var rate = m.DesiredRate ?? 200;

        var block = ActivatorUtilities.CreateInstance<AdaptiveFilterBlock>(
            sp, name, rate, alphaUi, off, mag, deriv);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "AdaptiveFilter";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
        => new object[] { Alpha, Offset, Magnitude, Deriviate };

    #endregion

    /// <summary>
    /// Processes incoming data: applies the adaptive filter to vectors or matrices.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="value">
    /// A <see cref="Vector{T}"/> (single sample) or <see cref="Matrix{T}"/>
    /// (rows = time, columns = channels).
    /// </param>
    /// <exception cref="InvalidOperationException">Thrown for unsupported input types.</exception>
    protected override void OnReceive(object sender, object value)
    {
        switch (value)
        {
            case Vector<double> v:
                Publish(Process(v));
                break;

            case Matrix<double> m:
                Publish(Process(m));
                break;

            default:
                throw new InvalidOperationException(
                    $"AdaptiveFilterBlock '{Name}' received unsupported input type: {value?.GetType().Name}");
        }
    }

    /// <summary>
    /// Processes a single sample vector through the adaptive filter.
    /// Updates internal state and <see cref="CutoffFrequency"/>.
    /// </summary>
    /// <param name="x">Input sample vector (one value per channel).</param>
    /// <returns>Filtered output vector of the same dimension.</returns>
    public Vector<double> Process(Vector<double> x)
    {
        if (_prevInput is null || _prevInput.Count != x.Count)
        {
            _prevInput = Vector<double>.Build.Dense(x.Count);
            _prevDerivative = Vector<double>.Build.Dense(x.Count);
            _prevOutput = Vector<double>.Build.Dense(x.Count);
        }

        var output = Vector<double>.Build.Dense(x.Count);

        double fs = _samplingFrequency;
        double a01 = Alpha / 10.0;
        double dcoef = (a01 * 2 * Math.PI / fs) / (1 + 2 * Math.PI * a01 / fs);
        double fc0 = 0;

        for (int i = 0; i < x.Count; i++)
        {
            double dx = (x[i] - _prevInput![i]) * fs;
            double dxFilt = dcoef * dx + (1 - dcoef) * _prevDerivative![i];
            _prevDerivative[i] = dxFilt;

            double fc = Math.Exp(
                Offset
                + Deriviate * Math.Abs(dxFilt)
                + Magnitude * Math.Abs(x[i]));

            double a = (2 * Math.PI * fc / fs) / (1 + 2 * Math.PI * fc / fs);
            double y = a * x[i] + (1 - a) * _prevOutput![i];

            _prevOutput[i] = y;
            _prevInput[i] = x[i];
            output[i] = y;

            if (i == 0) fc0 = fc;
        }

        CutoffFrequency = fc0;
        return output;
    }

    /// <summary>
    /// Processes a matrix of samples (rows = time steps, columns = channels).
    /// State is shared across rows so each column is filtered as a continuous channel.
    /// </summary>
    /// <param name="X">Input matrix.</param>
    /// <returns>Filtered output matrix of the same dimensions.</returns>
    public Matrix<double> Process(Matrix<double> X)
    {
        var cols = X.ColumnCount;
        if (_prevInput is null || _prevInput.Count != cols)
        {
            _prevInput = Vector<double>.Build.Dense(cols);
            _prevDerivative = Vector<double>.Build.Dense(cols);
            _prevOutput = Vector<double>.Build.Dense(cols);
        }

        var Y = Matrix<double>.Build.Dense(X.RowCount, cols);

        for (int r = 0; r < X.RowCount; r++)
        {
            var yRow = Process(X.Row(r));
            for (int c = 0; c < cols; c++) Y[r, c] = yRow[c];
        }

        return Y;
    }

    /// <summary>
    /// Releases base class resources (including CSV dumper if configured).
    /// </summary>
    public override void Dispose()
    {
        base.Dispose();
    }
}