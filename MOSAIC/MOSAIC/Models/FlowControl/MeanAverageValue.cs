using System;
using System.Collections.Generic;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Computes the Mean Absolute Value (MAV) feature for each channel in a windowed input signal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Formula:</b> MAV = (1/N) × Σ|x[i]| where N is the window size.
/// </para>
/// <para>
/// <b>Input:</b> A <see cref="Matrix{T}"/> of <see cref="double"/> where rows represent samples
/// and columns represent channels (typically produced by an upstream SlidingWindow block).
/// </para>
/// <para>
/// <b>Output:</b> A <see cref="Vector{T}"/> of <see cref="double"/> containing one MAV value
/// per channel, published to all downstream subscribers.
/// </para>
/// <para>
/// <b>Note:</b> This block computes a single fixed metric. For switchable metrics (RMS, IEMG,
/// VAR, etc.), use <see cref="MOSAIC.Models.Analytics.MetricsExtractor"/> instead.
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
///   "MAV": {
///     "Type": "MeanAverageValue",
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
public class MeanAverageValue : BaseBlock
{
    /// <summary>Visualization helper for binding the MAV output to the UI scope.</summary>
    public BlockVisualization? Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>
    /// Gets the number of output channels, determined from the column count of the first input matrix.
    /// </summary>
    public int OutputChannels { get; private set; }

    /// <summary>
    /// Gets the window size (row count) from the most recent input matrix.
    /// </summary>
    public int WindowSize { get; private set; }

    /// <summary>Gets the formula description for display in the UI.</summary>
    public string Description => "MAV = (1/N) × Σ|x[i]|";

    /// <summary>Gets a human-readable summary of the current configuration.</summary>
    public string ConfigSummary => OutputChannels > 0
        ? $"{OutputChannels} ch × {WindowSize} samples"
        : "Waiting for input...";

    /// <summary>
    /// Initializes a new <see cref="MeanAverageValue"/> block.
    /// </summary>
    /// <param name="name">Display name of the block.</param>
    /// <param name="desiredRate">Desired processing rate in Hz (typically inherited).</param>
    public MeanAverageValue(string name, double desiredRate = 0) : base(name, desiredRate) { }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_mav.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_mav";

    /// <summary>
    /// Creates a <see cref="MeanAverageValue"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">JSON model containing configuration.</param>
    /// <returns>A configured <see cref="MeanAverageValue"/> instance.</returns>
    public static MeanAverageValue ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var block = ActivatorUtilities.CreateInstance<MeanAverageValue>(sp, m.Name ?? "MeanAverageValue", m.DesiredRate ?? 0d);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "MeanAverageValue";

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
    /// Processes an incoming windowed matrix and publishes the computed MAV vector.
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

        if (WindowSize != rows)
        {
            WindowSize = rows;
            OnPropertyChanged(nameof(WindowSize));
            OnPropertyChanged(nameof(ConfigSummary));
        }

        if (OutputChannels != cols)
        {
            OutputChannels = cols;
            OnPropertyChanged(nameof(OutputChannels));
            OnPropertyChanged(nameof(ConfigSummary));
        }

        var mavVector = Vector<double>.Build.Dense(cols);

        for (int c = 0; c < cols; c++)
        {
            double sumAbs = 0.0;
            for (int r = 0; r < rows; r++)
                sumAbs += Math.Abs(window[r, c]);

            mavVector[c] = sumAbs / rows;
        }

        Publish(mavVector);
        Viz?.Feed(mavVector);
    }
}