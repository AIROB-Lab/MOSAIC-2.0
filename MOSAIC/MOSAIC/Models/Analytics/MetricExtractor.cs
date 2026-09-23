using System;
using System.Collections.Generic;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization;
using MOSAIC.Visualization.ScopeMonitor;

namespace MOSAIC.Models.Analytics;

/// <summary>
/// Available amplitude/error metric types.
/// </summary>
public enum MetricType
{
    /// <summary>Root Mean Square: √(Σx²/N)</summary>
    RMS,

    /// <summary>Mean Absolute Value: Σ|x|/N</summary>
    MAV,

    /// <summary>Integrated EMG / Sum of Absolute Values: Σ|x|</summary>
    IEMG,

    /// <summary>Simple Square Integral: Σx²</summary>
    SSI,

    /// <summary>Variance: Σ(x−μ)²/N</summary>
    VAR,

    /// <summary>Standard Deviation: √(Σ(x−μ)²/N)</summary>
    STD,

    /// <summary>Log Detector: e^(Σlog|x|/N)</summary>
    LOG,

    /// <summary>Peak (Maximum Absolute Value): max(|x|)</summary>
    PEAK,

    /// <summary>Peak-to-Peak: max(x) − min(x)</summary>
    P2P,

    /// <summary>Mean: Σx/N</summary>
    MEAN,

    /// <summary>Z-Score of last sample: (x[N] − μ) / σ per channel</summary>
    ZSCORE
}

/// <summary>
/// Computes amplitude/error metrics for each channel in a windowed input signal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Input:</b> A <see cref="Matrix{T}"/> of <see cref="double"/> where rows represent samples
/// and columns represent channels (typically produced by an upstream SlidingWindow block).
/// </para>
/// <para>
/// <b>Output:</b> A <see cref="Vector{T}"/> of <see cref="double"/> containing one metric value
/// per channel, published to all downstream subscribers.
/// </para>
/// <para>
/// The metric to compute is selected via the <see cref="Metric"/> property and defaults to
/// <see cref="MetricType.RMS"/>. Supported metrics include RMS, MAV, IEMG, SSI, VAR, STD, LOG,
/// PEAK, P2P, MEAN, and ZSCORE.
/// </para>
/// <para>
/// Window size and overlap are controlled entirely by the upstream SlidingWindow block,
/// keeping this block focused on the metric computation itself.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "MyMetrics": {
///     "Type": "MetricExtractor",
///     "Inputs": [ "SlidingWindow1" ],
///     "Params": [ "RMS" ],
///     "DesiredRate": 100
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader>
///     <term>Index</term>
///     <description>Description</description>
///   </listheader>
///   <item>
///     <term>0</term>
///     <description>
///       Metric type (string). One of: <c>RMS</c>, <c>MAV</c>, <c>IEMG</c>, <c>SSI</c>,
///       <c>VAR</c>, <c>STD</c>, <c>LOG</c>, <c>PEAK</c>, <c>P2P</c>, <c>MEAN</c>, <c>ZSCORE</c>.
///       Case-insensitive. Defaults to <c>RMS</c> if omitted.
///     </description>
///   </item>
/// </list>
/// </para>
/// </example>
public partial class MetricsExtractor : BaseBlock
{
    /// <summary>
    /// Scope for visualizing the output signal in the UI.
    /// </summary>
    public BlockVisualization? Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>
    /// Gets or sets the metric type to compute.
    /// </summary>
    [ObservableProperty]
    private MetricType _metric = MetricType.RMS;

    /// <summary>
    /// Gets the number of output channels, determined from the column count of the first input matrix received.
    /// </summary>
    [ObservableProperty]
    private int _outputChannels;

    /// <summary>
    /// Gets the window size (row count) from the most recent input matrix.
    /// </summary>
    [ObservableProperty]
    private int _windowSize;

    /// <summary>
    /// Initializes a new <see cref="MetricsExtractor"/> with the specified name, metric type, and optional desired rate.
    /// </summary>
    public MetricsExtractor(string name, MetricType metric = MetricType.RMS, double desiredRate = 0) : base(name, desiredRate)
    {
        _metric = metric;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "MetricExtractor";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
        => new object[] { Metric.ToString() };

    #endregion

    /// <summary>
    /// Creates and configures a <see cref="MetricsExtractor"/> from a JSON model definition.
    /// </summary>
    public static MetricsExtractor ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var metric = MetricType.RMS;

        if (m.Params is { Count: > 0 })
        {
            var param = m.Params[0];
            string? typeStr = null;

            if (param is JsonElement je)
                typeStr = je.GetString();
            else
                typeStr = param?.ToString();

            if (!string.IsNullOrEmpty(typeStr) && Enum.TryParse<MetricType>(typeStr, true, out var parsed))
                metric = parsed;
        }

        var block = ActivatorUtilities.CreateInstance<MetricsExtractor>(sp, m.Name ?? "AmplitudeMetrics", metric, m.DesiredRate ?? 0d);
        return block;
    }

    /// <summary>
    /// Releases resources used by this block, including the visualization scope.
    /// </summary>
    public override void Dispose()
    {
        base.Dispose();
        Viz.Dispose();
    }

    /// <summary>
    /// Processes an incoming windowed matrix and publishes the computed metric vector.
    /// </summary>
    protected override void OnReceive(object sender, object? value)
    {
        if (value is not Matrix<double> window)
        {
            ReportError("Expected a non-empty matrix of samples and channels.");
            return;
        }

        int rows = window.RowCount;
        int cols = window.ColumnCount;

        if (rows == 0 || cols == 0)
        {
            ReportError("Expected a non-empty matrix of samples and channels.");
            return;
        }

        if (WindowSize != rows)
            WindowSize = rows;

        if (OutputChannels != cols)
            OutputChannels = cols;

        var result = ComputeMetric(window, rows, cols);

        Publish(result);
        Viz?.Feed(result);
    }

    /// <summary>
    /// Dispatches metric computation for all channels based on the current <see cref="Metric"/> setting.
    /// </summary>
    private Vector<double> ComputeMetric(Matrix<double> window, int rows, int cols)
    {
        var result = Vector<double>.Build.Dense(cols);

        for (int c = 0; c < cols; c++)
        {
            result[c] = Metric switch
            {
                MetricType.RMS    => ComputeRMS(window, c, rows),
                MetricType.MAV    => ComputeMAV(window, c, rows),
                MetricType.IEMG   => ComputeIEMG(window, c, rows),
                MetricType.SSI    => ComputeSSI(window, c, rows),
                MetricType.VAR    => ComputeVAR(window, c, rows),
                MetricType.STD    => ComputeSTD(window, c, rows),
                MetricType.LOG    => ComputeLOG(window, c, rows),
                MetricType.PEAK   => ComputePEAK(window, c, rows),
                MetricType.P2P    => ComputeP2P(window, c, rows),
                MetricType.MEAN   => ComputeMEAN(window, c, rows),
                MetricType.ZSCORE => ComputeZSCORE(window, c, rows),
                _ => ComputeRMS(window, c, rows)
            };
        }

        return result;
    }

    #region Metric Computations

    /// <summary>
    /// Computes Root Mean Square: <c>√(Σx²/N)</c>.
    /// </summary>
    private static double ComputeRMS(Matrix<double> window, int col, int rows)
    {
        double sum = 0.0;
        for (int r = 0; r < rows; r++)
        {
            double val = window[r, col];
            sum += val * val;
        }
        return Math.Sqrt(sum / rows);
    }

    /// <summary>
    /// Computes Mean Absolute Value: <c>Σ|x|/N</c>.
    /// </summary>
    private static double ComputeMAV(Matrix<double> window, int col, int rows)
    {
        double sum = 0.0;
        for (int r = 0; r < rows; r++)
            sum += Math.Abs(window[r, col]);
        return sum / rows;
    }

    /// <summary>
    /// Computes Integrated EMG (sum of absolute values): <c>Σ|x|</c>.
    /// </summary>
    private static double ComputeIEMG(Matrix<double> window, int col, int rows)
    {
        double sum = 0.0;
        for (int r = 0; r < rows; r++)
            sum += Math.Abs(window[r, col]);
        return sum;
    }

    /// <summary>
    /// Computes Simple Square Integral: <c>Σx²</c>.
    /// </summary>
    private static double ComputeSSI(Matrix<double> window, int col, int rows)
    {
        double sum = 0.0;
        for (int r = 0; r < rows; r++)
        {
            double val = window[r, col];
            sum += val * val;
        }
        return sum;
    }

    /// <summary>
    /// Computes Variance: <c>Σ(x−μ)²/N</c> (population variance).
    /// </summary>
    private static double ComputeVAR(Matrix<double> window, int col, int rows)
    {
        double mean = ComputeMEAN(window, col, rows);
        double sum = 0.0;
        for (int r = 0; r < rows; r++)
        {
            double diff = window[r, col] - mean;
            sum += diff * diff;
        }
        return sum / rows;
    }

    /// <summary>
    /// Computes Standard Deviation: <c>√(Σ(x−μ)²/N)</c> (population standard deviation).
    /// </summary>
    private static double ComputeSTD(Matrix<double> window, int col, int rows)
    {
        return Math.Sqrt(ComputeVAR(window, col, rows));
    }

    /// <summary>
    /// Computes Log Detector: <c>e^(Σlog|x|/N)</c>.
    /// </summary>
    private static double ComputeLOG(Matrix<double> window, int col, int rows)
    {
        double sum = 0.0;
        int validCount = 0;
        for (int r = 0; r < rows; r++)
        {
            double absVal = Math.Abs(window[r, col]);
            if (absVal > 1e-10)
            {
                sum += Math.Log(absVal);
                validCount++;
            }
        }
        return validCount > 0 ? Math.Exp(sum / validCount) : 0.0;
    }

    /// <summary>
    /// Computes Peak (maximum absolute value): <c>max(|x|)</c>.
    /// </summary>
    private static double ComputePEAK(Matrix<double> window, int col, int rows)
    {
        double max = 0.0;
        for (int r = 0; r < rows; r++)
        {
            double absVal = Math.Abs(window[r, col]);
            if (absVal > max) max = absVal;
        }
        return max;
    }

    /// <summary>
    /// Computes Peak-to-Peak: <c>max(x) − min(x)</c>.
    /// </summary>
    private static double ComputeP2P(Matrix<double> window, int col, int rows)
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        for (int r = 0; r < rows; r++)
        {
            double val = window[r, col];
            if (val < min) min = val;
            if (val > max) max = val;
        }
        return max - min;
    }

    /// <summary>
    /// Computes arithmetic Mean: <c>Σx/N</c>.
    /// </summary>
    private static double ComputeMEAN(Matrix<double> window, int col, int rows)
    {
        double sum = 0.0;
        for (int r = 0; r < rows; r++)
            sum += window[r, col];
        return sum / rows;
    }

    /// <summary>
    /// Computes Z-Score of the last sample relative to the window:
    /// <c>(x[N-1] − μ) / σ</c> where μ and σ are computed over the full window.
    /// </summary>
    /// <remarks>
    /// Returns <c>0</c> if the standard deviation is effectively zero (constant signal).
    /// </remarks>
    private static double ComputeZSCORE(Matrix<double> window, int col, int rows)
    {
        double mean = ComputeMEAN(window, col, rows);
        double std  = Math.Sqrt(ComputeVAR(window, col, rows));

        if (std < 1e-12) return 0.0;

        return (window[rows - 1, col] - mean) / std;
    }

    #endregion
}