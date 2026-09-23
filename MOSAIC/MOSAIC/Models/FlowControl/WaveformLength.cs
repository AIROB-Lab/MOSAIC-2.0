using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Computes the Waveform Length (WL) feature for each channel in a windowed input signal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Formula:</b> WL = Σ|x[n] − x[n−1]| for n = 1 to N−1, where N is the window size.
/// </para>
/// <para>
/// <b>Input:</b> A <see cref="Matrix{T}"/> of <see cref="double"/> where rows represent samples
/// and columns represent channels (typically from an upstream SlidingWindow block).
/// </para>
/// <para>
/// <b>Output:</b> A <see cref="Vector{T}"/> of <see cref="double"/> containing one WL value
/// per channel, published to all downstream subscribers.
/// </para>
/// <para>
/// Waveform Length is a cumulative measure of signal complexity — it captures both amplitude
/// and frequency information by summing consecutive sample-to-sample differences.
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs the output vector via <see cref="BaseBlock.Publish"/>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "WL": {
///     "Type": "WaveformLength",
///     "Inputs": [ "SlidingWindow1" ],
///     "DesiredRate": 100,
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b> None.
/// </para>
/// </example>
public partial class WaveformLength : BaseBlock
{
    /// <summary>Visualization helper for binding the WL output to the UI scope.</summary>
    public BlockVisualization? Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>
    /// Gets the number of output channels, determined from the column count of the input matrix.
    /// </summary>
    [ObservableProperty]
    private int _outputChannels;

    /// <summary>
    /// Gets the window size (row count) from the most recent input matrix.
    /// </summary>
    [ObservableProperty]
    private int _windowSize;

    /// <summary>
    /// Initializes a new <see cref="WaveformLength"/> block.
    /// </summary>
    /// <param name="name">Display name of the block.</param>
    /// <param name="desiredRate">Desired processing rate in Hz (typically inherited).</param>
    public WaveformLength(string name, double desiredRate = 0) : base(name, desiredRate) { }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_wl.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_wl";

    /// <summary>
    /// Creates a <see cref="WaveformLength"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">JSON model containing configuration.</param>
    /// <returns>A configured <see cref="WaveformLength"/> instance.</returns>
    public static WaveformLength ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var block = ActivatorUtilities.CreateInstance<WaveformLength>(sp, m.Name ?? "WaveformLength", m.DesiredRate ?? 0d);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "WaveformLength";

    #endregion

    /// <summary>
    /// Releases visualization and base class resources (including CSV dumper if configured).
    /// </summary>
    public override void Dispose()
    {
        base.Dispose();
        Viz?.Dispose();
    }

    /// <summary>
    /// Processes an incoming windowed matrix and publishes the computed WL vector.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="value">
    /// Expected to be a <see cref="Matrix{T}"/> of <see cref="double"/>
    /// (rows = samples, columns = channels). Non-matrix inputs are rejected and reported
    /// through <see cref="BaseBlock.ReportError"/>; activity status remains automatic.
    /// </param>
    protected override void OnReceive(object sender, object? value)
    {
        if (value is not Matrix<double> window)
        {
            ReportError("Expected a matrix of samples and channels.");
            return;
        }

        int rows = window.RowCount;
        int cols = window.ColumnCount;

        if (WindowSize != rows) WindowSize = rows;
        if (OutputChannels != cols) OutputChannels = cols;

        var wlVector = Vector<double>.Build.Dense(cols);

        for (int c = 0; c < cols; c++)
        {
            double wlSum = 0.0;
            for (int r = 1; r < rows; r++)
                wlSum += Math.Abs(window[r, c] - window[r - 1, c]);
            wlVector[c] = wlSum;
        }

        Viz?.Feed(wlVector);
        Publish(wlVector);
    }
}