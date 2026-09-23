using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Basics;

namespace MOSAIC.Models.Analytics;

/// <summary>
/// Supervised version of <see cref="OnlineICA"/> with manual cluster labelling and capture.
/// </summary>
/// <remarks>
/// <para>
/// Extends <see cref="OnlineICA"/> by intercepting projections via
/// <see cref="PublishProjection"/> and storing them in labelled clusters for supervised
/// learning or visualisation purposes.
/// </para>
/// <para>
/// <b>Usage:</b>
/// <list type="number">
///     <item><description>Call <see cref="StartCapture"/> with a label to begin capturing.</description></item>
///     <item><description>Process data normally — projections are automatically stored.</description></item>
///     <item><description>Call <see cref="StopCapture"/> to end the capture session.</description></item>
///     <item><description>Access captured data via <see cref="CapturedClusters"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// All ICA parameters (contrast function, whitening mode, etc.) are inherited from
/// <see cref="OnlineICA"/>. See the base class for algorithmic details.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "MySupervisedICA": {
///     "Type": "SupervisedICA",
///     "Inputs": [ "FeatureExtractor" ],
///     "Params": [ 3, 0.1, 50, 1000, 0, 500, 1, 0, 0.005 ],
///     "DesiredRate": 1000,
///     "Path": "output/sica_log"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b> Identical to <see cref="OnlineICA"/>:
/// <list type="table">
///   <listheader>
///     <term>Index</term>
///     <description>Description</description>
///   </listheader>
///   <item>
///     <term>0</term>
///     <description><c>k</c> (int) — Number of independent components. Default: <c>2</c>.</description>
///   </item>
///   <item>
///     <term>1</term>
///     <description><c>eta0</c> (double) — Initial learning rate. Default: <c>0.1</c>.</description>
///   </item>
///   <item>
///     <term>2</term>
///     <description><c>reorthEvery</c> (int) — Orthonormalisation frequency. Default: <c>50</c>.</description>
///   </item>
///   <item>
///     <term>3</term>
///     <description><c>minStableCount</c> (int) — Minimum samples before stable. Default: <c>1000</c>.</description>
///   </item>
///   <item>
///     <term>4</term>
///     <description><c>contrastFunction</c> (double) — <c>0</c>=LogCosh, <c>1</c>=Exp, <c>2</c>=Cube. Default: <c>0</c>.</description>
///   </item>
///   <item>
///     <term>5</term>
///     <description><c>warmupSamples</c> (int) — Warm-up samples. Default: <c>500</c>.</description>
///   </item>
///   <item>
///     <term>6</term>
///     <description><c>freezeWhiteningAfterWarmup</c> (int) — <c>1</c>=freeze, <c>0</c>=keep updating. Default: <c>1</c>.</description>
///   </item>
///   <item>
///     <term>7</term>
///     <description><c>adaptiveWhitening</c> (int) — <c>1</c>=adaptive, <c>0</c>=off. Default: <c>0</c>.</description>
///   </item>
///   <item>
///     <term>8</term>
///     <description><c>covarianceDecay</c> (double) — EWMA decay factor. Default: <c>0.005</c>.</description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Path:</b> Optional. If provided, CSV logs of projected outputs are written to this directory.
/// </para>
/// </example>
public partial class SupervisedICA : OnlineICA
{
    /// <inheritdoc />
    public override int MinInputs => 1;

    /// <inheritdoc />
    public override int MaxInputs => 2;

    #region Cluster Storage

    /// <summary>
    /// Stores captured cluster points keyed by class label, used for UI scatter-plot visualisation.
    /// </summary>
    /// <value>Dictionary mapping cluster labels to lists of k-dimensional projection vectors.</value>
    public Dictionary<string, List<Vector<double>>> CapturedClusters { get; } = new();

    /// <summary>
    /// Hex colour strings assigned to each cluster label for consistent UI rendering.
    /// </summary>
    /// <value>Dictionary mapping cluster labels to hex color strings (e.g., <c>"#FF6B6B"</c>).</value>
    public Dictionary<string, string> ClusterColors { get; } = new();

    /// <summary>Palette of distinct colours cycled through as new clusters appear.</summary>
    private readonly List<string> _availableColors = new()
    {
        "#FF6B6B", "#4ECDC4", "#45B7D1", "#FFA07A", "#98D8C8", "#F7DC6F",
        "#BB8FCE", "#85C1E2", "#F8B88B", "#AAB7B8", "#52C7B8", "#FFB6C1",
    };

    /// <summary>Next colour index in <see cref="_availableColors"/>.</summary>
    private int _colorIndex = 0;

    #endregion

    #region Observable Properties

    /// <summary>
    /// Gets or sets whether the block is currently capturing data into a labelled cluster.
    /// </summary>
    [ObservableProperty]
    private bool _isCapturing = false;

    /// <summary>
    /// Gets or sets the class label currently being captured.
    /// </summary>
    [ObservableProperty]
    private string _currentLabel = "";

    /// <summary>
    /// Gets or sets the number of points captured in the current session.
    /// </summary>
    [ObservableProperty]
    private int _currentCaptureCount = 0;

    /// <summary>
    /// Gets or sets whether to preserve captured clusters when <see cref="Reset"/> is called.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/> (default), calling <see cref="Reset"/> preserves all captured
    /// cluster data while resetting only the ICA model parameters.
    /// </remarks>
    [ObservableProperty]
    private bool _preserveClustersOnReset = true;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="SupervisedICA"/> class.
    /// </summary>
    /// <param name="name">Unique identifier for this processing block.</param>
    /// <param name="desiredRate">Target processing rate in Hz (0 = inherited from upstream).</param>
    /// <param name="k">Number of independent components to extract.</param>
    /// <param name="eta0">Initial learning rate η₀ ∈ (0, 1].</param>
    /// <param name="reorthEvery">Orthonormalisation frequency (samples).</param>
    /// <param name="minStableCount">Minimum samples before model is considered stable.</param>
    /// <param name="contrastFunction">Contrast function: 0=LogCosh, 1=Exp, 2=Cube.</param>
    /// <param name="warmupSamples">Number of samples for warm-up phase (whitening estimation).</param>
    /// <param name="freezeWhiteningAfterWarmup">If true, whitening is frozen after warm-up.</param>
    /// <param name="adaptiveWhitening">If true, enables adaptive whitening for EMG/ultrasound.</param>
    /// <param name="covarianceDecay">Decay factor for EWMA covariance in adaptive mode.</param>
    public SupervisedICA(
        string name,
        double desiredRate = 0,
        int k = 2,
        double eta0 = 0.1,
        int reorthEvery = 50,
        int minStableCount = 1000,
        double contrastFunction = 0.0,
        int warmupSamples = 500,
        bool freezeWhiteningAfterWarmup = true,
        bool adaptiveWhitening = false,
        double covarianceDecay = 0.005)
        : base(name, desiredRate, k, eta0, reorthEvery, minStableCount, contrastFunction, warmupSamples, freezeWhiteningAfterWarmup, adaptiveWhitening, covarianceDecay)
    {
        Console.WriteLine($"[SupervisedICA '{Name}'] Initialized with manual cluster labeling");
    }

    #endregion

    #region Factory Method

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_projections.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_projections";

    /// <summary>
    /// Creates a <see cref="SupervisedICA"/> instance from a JSON model definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// The JSON model. See class-level <c>&lt;example&gt;</c> for expected Params layout
    /// (identical to <see cref="OnlineICA"/>).
    /// </param>
    /// <returns>A fully configured <see cref="SupervisedICA"/> instance.</returns>
    public static new SupervisedICA ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "SupervisedICA";
        var rate = m.DesiredRate ?? 0;

        int k = TryInt(m.Params, 0, 2);
        double eta0 = TryDouble(m.Params, 1, 0.1);
        int reorth = TryInt(m.Params, 2, 50);
        int minStable = TryInt(m.Params, 3, 1000);
        double contrast = TryDouble(m.Params, 4, 0.0);
        int warmup = TryInt(m.Params, 5, 500);
        bool freezeWhitening = TryInt(m.Params, 6, 1) != 0;
        bool adaptiveWhitening = TryInt(m.Params, 7, 0) != 0;
        double covDecay = TryDouble(m.Params, 8, 0.005);

        var block = new SupervisedICA(name, rate, k, eta0, reorth, minStable, contrast, warmup, freezeWhitening, adaptiveWhitening, covDecay);
        return block;
    }

    #endregion

    #region Capture Control

    /// <summary>
    /// Starts capturing projected data with the given label.
    /// </summary>
    /// <param name="label">The cluster label for captured points.</param>
    /// <remarks>
    /// <para>
    /// If already capturing, the current capture session is stopped first.
    /// If the label already exists, new points are appended to the existing cluster.
    /// </para>
    /// <para>
    /// A unique colour is automatically assigned to new clusters from the internal palette.
    /// </para>
    /// </remarks>
    public void StartCapture(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            Console.WriteLine($"[SupervisedICA '{Name}'] Cannot start capture with empty label");
            return;
        }

        if (IsCapturing)
        {
            Console.WriteLine($"[SupervisedICA '{Name}'] Already capturing '{CurrentLabel}', stopping first");
            StopCapture();
        }

        IsCapturing = true;
        CurrentLabel = label.Trim();
        CurrentCaptureCount = 0;

        if (!CapturedClusters.ContainsKey(CurrentLabel))
        {
            CapturedClusters[CurrentLabel] = new List<Vector<double>>();

            ClusterColors[CurrentLabel] = _availableColors[_colorIndex % _availableColors.Count];
            _colorIndex++;

            Console.WriteLine($"[SupervisedICA '{Name}'] Started capturing new cluster '{CurrentLabel}' with color {ClusterColors[CurrentLabel]}");
        }
        else
        {
            Console.WriteLine($"[SupervisedICA '{Name}'] Resuming capture for existing cluster '{CurrentLabel}' (currently {CapturedClusters[CurrentLabel].Count} points)");
        }

        OnPropertyChanged(nameof(CapturedClusters));
    }

    /// <summary>
    /// Stops the current capture session.
    /// </summary>
    /// <remarks>
    /// If not currently capturing, this method is a no-op (logged as a warning).
    /// </remarks>
    public void StopCapture()
    {
        if (!IsCapturing)
        {
            Console.WriteLine($"[SupervisedICA '{Name}'] Not currently capturing");
            return;
        }

        var count = CapturedClusters.ContainsKey(CurrentLabel)
            ? CapturedClusters[CurrentLabel].Count
            : 0;

        Console.WriteLine($"[SupervisedICA '{Name}'] Stopped capturing '{CurrentLabel}'. Total points: {count} (captured this session: {CurrentCaptureCount})");

        IsCapturing = false;
        CurrentLabel = "";
        CurrentCaptureCount = 0;

        OnPropertyChanged(nameof(CapturedClusters));
    }

    /// <summary>
    /// Removes a single cluster and its colour assignment.
    /// </summary>
    /// <param name="label">The label of the cluster to remove.</param>
    public void ClearCluster(string label)
    {
        if (CapturedClusters.ContainsKey(label))
        {
            var count = CapturedClusters[label].Count;
            CapturedClusters.Remove(label);
            ClusterColors.Remove(label);

            Console.WriteLine($"[SupervisedICA '{Name}'] Cleared cluster '{label}' ({count} points)");

            OnPropertyChanged(nameof(CapturedClusters));
        }
    }

    /// <summary>
    /// Removes all captured clusters and resets the colour index.
    /// </summary>
    public void ClearAllClusters()
    {
        var totalCount = CapturedClusters.Sum(c => c.Value.Count);
        var clusterCount = CapturedClusters.Count;

        CapturedClusters.Clear();
        ClusterColors.Clear();
        _colorIndex = 0;

        Console.WriteLine($"[SupervisedICA '{Name}'] Cleared all clusters ({clusterCount} clusters, {totalCount} total points)");

        OnPropertyChanged(nameof(CapturedClusters));
    }

    #endregion

    #region Projection Override

    /// <summary>
    /// Intercepts projections from the base <see cref="OnlineICA"/> to capture them into
    /// the current cluster when capture is active, then publishes downstream.
    /// </summary>
    /// <param name="projection">The k-dimensional independent component vector.</param>
    /// <remarks>
    /// A clone of the projection is stored in <see cref="CapturedClusters"/> under
    /// <see cref="CurrentLabel"/>. The original vector is always forwarded to downstream
    /// subscribers via <see cref="BaseBlock.Publish"/>.
    /// </remarks>
    protected override void PublishProjection(Vector<double> projection)
    {
        if (IsCapturing && !string.IsNullOrEmpty(CurrentLabel))
        {
            CapturedClusters[CurrentLabel].Add(projection.Clone());
            CurrentCaptureCount++;

            if (CurrentCaptureCount % 50 == 0)
            {
                Console.WriteLine($"[SupervisedICA '{Name}'] Captured {CurrentCaptureCount} points for '{CurrentLabel}'");
            }

            if (CurrentCaptureCount % 10 == 0)
            {
                OnPropertyChanged(nameof(CurrentCaptureCount));
            }
        }

        base.PublishProjection(projection);
    }

    #endregion

    #region Reset Override

    /// <summary>
    /// Resets the ICA model, optionally preserving captured clusters.
    /// </summary>
    /// <remarks>
    /// Behaviour depends on <see cref="PreserveClustersOnReset"/>:
    /// <list type="bullet">
    ///     <item><description>If <see langword="true"/> (default): ICA parameters are reset; clusters are preserved.</description></item>
    ///     <item><description>If <see langword="false"/>: Everything is reset including all clusters and colours.</description></item>
    /// </list>
    /// </remarks>
    public new void Reset()
    {
        base.Reset();

        if (!PreserveClustersOnReset)
        {
            CapturedClusters.Clear();
            ClusterColors.Clear();
            _colorIndex = 0;
            Console.WriteLine($"[SupervisedICA '{Name}'] Clusters cleared on reset");
        }
        else
        {
            Console.WriteLine($"[SupervisedICA '{Name}'] Clusters preserved on reset ({CapturedClusters.Count} clusters, {CapturedClusters.Sum(c => c.Value.Count)} points)");
        }

        OnPropertyChanged(nameof(CapturedClusters));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Attempts to parse an integer from a parameter list.
    /// </summary>
    /// <param name="p">Parameter list.</param>
    /// <param name="idx">Index to read.</param>
    /// <param name="def">Default value if parsing fails.</param>
    /// <returns>Parsed integer or default.</returns>
    private static int TryInt(IReadOnlyList<object>? p, int idx, int def)
    {
        if (p == null || idx >= p.Count) return def;

        return p[idx] switch
        {
            int i => i,
            double d => (int)Math.Round(d),
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetInt32(),
            JsonElement { ValueKind: JsonValueKind.String } je when int.TryParse(je.GetString(), out var v) => v,
            _ => def
        };
    }

    /// <summary>
    /// Attempts to parse a double from a parameter list.
    /// </summary>
    /// <param name="p">Parameter list.</param>
    /// <param name="idx">Index to read.</param>
    /// <param name="def">Default value if parsing fails.</param>
    /// <returns>Parsed double or default.</returns>
    private static double TryDouble(IReadOnlyList<object>? p, int idx, double def)
    {
        if (p == null || idx >= p.Count) return def;

        return p[idx] switch
        {
            double d => d,
            float f => f,
            int i => i,
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } je when double.TryParse(je.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var v) => v,
            _ => def
        };
    }

    #endregion
}