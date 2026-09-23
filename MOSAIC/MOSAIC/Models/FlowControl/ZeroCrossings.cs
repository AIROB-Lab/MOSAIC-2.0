using System;
using System.Collections.Generic;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Computes the Zero Crossings (ZC) feature for each channel in a windowed input signal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Formula:</b> ZC = Σ sgn(−x[n] × x[n+1]) where |x[n] − x[n+1]| ≥ threshold.
/// </para>
/// <para>
/// <b>Input:</b> A <see cref="Matrix{T}"/> of <see cref="double"/> where rows represent samples
/// and columns represent channels (typically from an upstream SlidingWindow block).
/// </para>
/// <para>
/// <b>Output:</b> A <see cref="Vector{T}"/> of <see cref="double"/> containing one ZC count
/// per channel, published to all downstream subscribers.
/// </para>
/// <para>
/// <b>Threshold:</b> The configurable <see cref="Threshold"/> parameter suppresses noise by
/// requiring the absolute difference between consecutive samples to exceed a minimum magnitude
/// before counting a zero crossing. A threshold of 0 counts all sign changes through zero.
/// </para>
/// <para>
/// Zero Crossings is a simple frequency-domain proxy — higher ZC counts indicate higher
/// dominant frequency content in the signal.
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
///   "ZC": {
///     "Type": "ZeroCrossings",
///     "Inputs": [ "SlidingWindow1" ],
///     "Params": [ 0.01 ],
///     "DesiredRate": 100,
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description>
///     <c>Threshold</c> (double, optional). Minimum absolute difference between consecutive
///     samples to count a zero crossing. Defaults to <c>0.0</c>.
///   </description></item>
/// </list>
/// </para>
/// </example>
public partial class ZeroCrossings : BaseBlock
{
    /// <summary>Visualization helper for binding the ZC output to the UI scope.</summary>
    public BlockVisualization? Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>
    /// Gets or sets the minimum amplitude difference threshold for noise suppression.
    /// Consecutive samples must differ by at least this amount to count as a zero crossing.
    /// </summary>
    [ObservableProperty]
    private double _threshold;

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
    /// Initializes a new <see cref="ZeroCrossings"/> block.
    /// </summary>
    /// <param name="name">Display name of the block.</param>
    /// <param name="threshold">Minimum difference threshold for noise suppression.</param>
    /// <param name="desiredRate">Desired processing rate in Hz (typically inherited).</param>
    public ZeroCrossings(string name, double threshold = 0.0, double desiredRate = 0)
        : base(name, desiredRate)
    {
        _threshold = threshold;
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_zc.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_zc";

    /// <summary>
    /// Creates a <see cref="ZeroCrossings"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// JSON model. <c>Params[0]</c> is the optional threshold value.
    /// See the class-level example.
    /// </param>
    /// <returns>A configured <see cref="ZeroCrossings"/> instance.</returns>
    public static ZeroCrossings ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var threshold = m.Params is { Count: > 0 }
            ? JsonModel.GetDouble(m.Params[0], 0.0)
            : 0.0;

        var block = ActivatorUtilities.CreateInstance<ZeroCrossings>(
            sp, m.Name ?? "ZeroCrossings", threshold, m.DesiredRate ?? 0d);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "ZeroCrossings";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
        => new object[] { Threshold };

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
    /// Processes an incoming windowed matrix and publishes the computed ZC vector.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="value">
    /// Expected to be a <see cref="Matrix{T}"/> of <see cref="double"/>
    /// (rows = samples, columns = channels). Non-matrix inputs are rejected and reported
    /// through <see cref="BaseBlock.ReportError"/>; activity status remains automatic.
    /// </param>
    /// <remarks>
    /// For each channel, the algorithm iterates consecutive sample pairs and counts positions
    /// where the sign changes (one positive, one negative), provided the absolute difference
    /// exceeds <see cref="Threshold"/>.
    /// </remarks>
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

        var zcVector = Vector<double>.Build.Dense(cols);
        double thresh = Threshold;

        for (int c = 0; c < cols; c++)
        {
            int zeroCrossings = 0;

            for (int r = 0; r < rows - 1; r++)
            {
                double current = window[r, c];
                double next = window[r + 1, c];

                if ((current > 0 && next < 0) || (current < 0 && next > 0))
                {
                    if (Math.Abs(current - next) >= thresh)
                        zeroCrossings++;
                }
            }

            zcVector[c] = zeroCrossings;
        }

        Viz?.Feed(zcVector);
        Publish(zcVector);
    }
}
