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
/// Computes the Slope Sign Changes (SSC) feature for each channel in a windowed input signal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Formula:</b> SSC = Σ f[(x[n] − x[n−1]) × (x[n] − x[n+1])]
/// where f[x] = 1 if |diff₁| ≥ threshold and |diff₂| ≥ threshold and diff₁ × diff₂ &lt; 0,
/// else 0.
/// </para>
/// <para>
/// <b>Input:</b> A <see cref="Matrix{T}"/> of <see cref="double"/> where rows represent samples
/// and columns represent channels (typically from an upstream SlidingWindow block).
/// </para>
/// <para>
/// <b>Output:</b> A <see cref="Vector{T}"/> of <see cref="double"/> containing one SSC count
/// per channel, published to all downstream subscribers.
/// </para>
/// <para>
/// <b>Threshold:</b> The configurable <see cref="Threshold"/> parameter suppresses noise by
/// requiring both adjacent differences to exceed a minimum magnitude before counting a sign change.
/// A threshold of 0 counts all sign changes.
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
///   "SSC": {
///     "Type": "SlopeSignChanges",
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
///     <c>Threshold</c> (double, optional). Minimum absolute difference required for both
///     adjacent samples to count a slope sign change. Defaults to <c>0.0</c>.
///   </description></item>
/// </list>
/// </para>
/// </example>
public partial class SlopeSignChanges : BaseBlock
{
    /// <summary>Visualization helper for binding the SSC output to the UI scope.</summary>
    public BlockVisualization? Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>
    /// Gets or sets the minimum difference threshold for noise suppression.
    /// Both adjacent differences must exceed this value to count as a sign change.
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

    /// <summary>Gets the formula description for display in the UI.</summary>
    public string Description => "SSC = count of slope sign changes";

    /// <summary>Gets a human-readable summary of the current configuration.</summary>
    public string ConfigSummary => OutputChannels > 0
        ? $"{OutputChannels} ch × {WindowSize} samples (threshold={Threshold:G3})"
        : "Waiting for input...";

    /// <summary>
    /// Initializes a new <see cref="SlopeSignChanges"/> block.
    /// </summary>
    /// <param name="name">Display name of the block.</param>
    /// <param name="threshold">Minimum difference threshold for noise suppression.</param>
    /// <param name="desiredRate">Desired processing rate in Hz (typically inherited).</param>
    public SlopeSignChanges(string name, double threshold = 0.0, double desiredRate = 0)
        : base(name, desiredRate)
    {
        _threshold = threshold;
    }

    /// <summary>Notifies <see cref="ConfigSummary"/> when threshold changes.</summary>
    partial void OnThresholdChanged(double value) => NotifyConfigChanged();

    /// <summary>Notifies <see cref="ConfigSummary"/> when output channels change.</summary>
    partial void OnOutputChannelsChanged(int value) => NotifyConfigChanged();

    /// <summary>Notifies <see cref="ConfigSummary"/> when window size changes.</summary>
    partial void OnWindowSizeChanged(int value) => NotifyConfigChanged();

    /// <summary>Raises property-changed for the computed <see cref="ConfigSummary"/>.</summary>
    private void NotifyConfigChanged() => OnPropertyChanged(nameof(ConfigSummary));

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_ssc.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_ssc";

    /// <summary>
    /// Creates a <see cref="SlopeSignChanges"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// JSON model. <c>Params[0]</c> is the optional threshold value.
    /// See the class-level example.
    /// </param>
    /// <returns>A configured <see cref="SlopeSignChanges"/> instance.</returns>
    public static SlopeSignChanges ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        double threshold = 0.0;
        if (m.Params is { Count: > 0 })
        {
            var param = m.Params[0];
            if (param is JsonElement je)
                threshold = je.GetDouble();
            else if (param is double d)
                threshold = d;
            else if (double.TryParse(param?.ToString(), out var parsed))
                threshold = parsed;
        }

        var block = ActivatorUtilities.CreateInstance<SlopeSignChanges>(
            sp, m.Name ?? "SlopeSignChanges", threshold, m.DesiredRate ?? 0d);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "SlopeSignChanges";

    /// <inheritdoc />
    protected override IReadOnlyList<object> GetJsonParams()
        => [Threshold];

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
    /// Processes an incoming windowed matrix and publishes the computed SSC vector.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="value">
    /// Expected to be a <see cref="Matrix{T}"/> of <see cref="double"/>
    /// (rows = samples, columns = channels). Non-matrix inputs are rejected and reported
    /// through <see cref="BaseBlock.ReportError"/>; activity status remains automatic.
    /// </param>
    /// <remarks>
    /// For each channel, the algorithm iterates samples [1, N−2] and counts positions
    /// where the sign of the first difference flips (relative to the previous difference),
    /// provided both differences exceed <see cref="Threshold"/>.
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

        if (WindowSize != rows)
            WindowSize = rows;

        if (OutputChannels != cols)
            OutputChannels = cols;

        var sscVector = Vector<double>.Build.Dense(cols);
        double thresh = Threshold;

        for (int c = 0; c < cols; c++)
        {
            int slopeChanges = 0;

            for (int r = 1; r < rows - 1; r++)
            {
                double diff1 = window[r, c] - window[r - 1, c];
                double diff2 = window[r + 1, c] - window[r, c];

                if ((diff1 * diff2 < 0) && (Math.Abs(diff1) >= thresh) && (Math.Abs(diff2) >= thresh))
                    slopeChanges++;
            }

            sscVector[c] = slopeChanges;
        }

        Viz?.Feed(sscVector);
        Publish(sscVector);
    }
}