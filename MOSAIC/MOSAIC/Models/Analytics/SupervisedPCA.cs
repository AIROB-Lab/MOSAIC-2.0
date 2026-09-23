using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;

namespace MOSAIC.Models.Analytics;

/// <summary>
/// Supervised version of <see cref="OnlinePCA"/> with manual cluster labelling and capture.
/// </summary>
/// <remarks>
/// <para>
/// Extends <see cref="OnlinePCA"/> by intercepting projections via
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
/// All PCA parameters (learning rate, orthonormalisation, etc.) are inherited from
/// <see cref="OnlinePCA"/>. See the base class for algorithmic details.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "MySupervisedPCA": {
///     "Type": "SupervisedPCA",
///     "Inputs": [ "FeatureExtractor" ],
///     "Params": [ 2, 0.2, 100, 500 ],
///     "DesiredRate": 100,
///     "Path": "output/spca_log"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b> Identical to <see cref="OnlinePCA"/>:
/// <list type="table">
///   <listheader>
///     <term>Index</term>
///     <description>Description</description>
///   </listheader>
///   <item>
///     <term>0</term>
///     <description><c>k</c> (int) — Number of principal components (2 or 3). Default: <c>2</c>.</description>
///   </item>
///   <item>
///     <term>1</term>
///     <description><c>eta0</c> (double) — Initial learning rate η₀. Default: <c>0.2</c>.</description>
///   </item>
///   <item>
///     <term>2</term>
///     <description><c>reorthEvery</c> (int) — QR orthonormalisation frequency. Default: <c>100</c>.</description>
///   </item>
///   <item>
///     <term>3</term>
///     <description><c>minStableCount</c> (int) — Minimum samples before stable. Default: <c>500</c>.</description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Path:</b> Optional. If provided, CSV logs of projected outputs are written to this directory.
/// </para>
/// </example>
public partial class SupervisedPCA : OnlinePCA
{
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
    /// cluster data while resetting only the PCA model parameters.
    /// </remarks>
    [ObservableProperty]
    private bool _preserveClustersOnReset = true;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new <see cref="SupervisedPCA"/> instance.
    /// </summary>
    /// <param name="name">Display name of the block.</param>
    /// <param name="desiredRate">Desired processing rate in Hz. 0 = inherited from upstream.</param>
    /// <param name="k">Number of principal components (must be 2 or 3 for visualisation).</param>
    /// <param name="eta0">Initial learning rate η₀.</param>
    /// <param name="reorthEvery">QR orthonormalisation frequency in samples.</param>
    /// <param name="minStableCount">Minimum samples before the model is considered stable.</param>
    public SupervisedPCA(
        string name,
        double desiredRate = 0,
        int k = 2,
        double eta0 = 0.2,
        int reorthEvery = 100,
        int minStableCount = 500)
        : base(name, desiredRate, k, eta0, reorthEvery, minStableCount)
    {
        Console.WriteLine($"[SupervisedPCA '{Name}'] Initialized with manual cluster labeling");
    }

    #endregion

    #region Factory Method

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_projection.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_projection";

    /// <summary>
    /// Creates a <see cref="SupervisedPCA"/> instance from a JSON model definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// The JSON model. See class-level <c>&lt;example&gt;</c> for expected Params layout
    /// (identical to <see cref="OnlinePCA"/>).
    /// </param>
    /// <returns>A fully configured <see cref="SupervisedPCA"/> instance.</returns>
    public static new SupervisedPCA ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "SupervisedPCA";
        var rate = m.DesiredRate ?? 0;

        int k = TryInt(m.Params, 0, 2);
        double eta0 = TryDouble(m.Params, 1, 0.2);
        int reorth = TryInt(m.Params, 2, 100);
        int minStable = TryInt(m.Params, 3, 500);
        
        var block = new SupervisedPCA(name, rate, k, eta0, reorth, minStable);
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
    /// If already capturing, the current session is stopped first.
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
            Console.WriteLine($"[SupervisedPCA '{Name}'] Cannot start capture with empty label");
            return;
        }

        if (IsCapturing)
        {
            Console.WriteLine($"[SupervisedPCA '{Name}'] Already capturing '{CurrentLabel}', stopping first");
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

            Console.WriteLine($"[SupervisedPCA '{Name}'] Started capturing new cluster '{CurrentLabel}' with color {ClusterColors[CurrentLabel]}");
        }
        else
        {
            Console.WriteLine($"[SupervisedPCA '{Name}'] Resuming capture for existing cluster '{CurrentLabel}' (currently {CapturedClusters[CurrentLabel].Count} points)");
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
            Console.WriteLine($"[SupervisedPCA '{Name}'] Not currently capturing");
            return;
        }

        var count = CapturedClusters.ContainsKey(CurrentLabel)
            ? CapturedClusters[CurrentLabel].Count
            : 0;

        Console.WriteLine($"[SupervisedPCA '{Name}'] Stopped capturing '{CurrentLabel}'. Total points: {count} (captured this session: {CurrentCaptureCount})");

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

            Console.WriteLine($"[SupervisedPCA '{Name}'] Cleared cluster '{label}' ({count} points)");

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

        Console.WriteLine($"[SupervisedPCA '{Name}'] Cleared all clusters ({clusterCount} clusters, {totalCount} total points)");

        OnPropertyChanged(nameof(CapturedClusters));
    }

    #endregion

    #region Projection Override

    /// <summary>
    /// Intercepts projections from the base <see cref="OnlinePCA"/> to capture them into
    /// the current cluster when capture is active, then publishes downstream.
    /// </summary>
    /// <param name="projection">The k-dimensional principal component projection vector.</param>
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
                Console.WriteLine($"[SupervisedPCA '{Name}'] Captured {CurrentCaptureCount} points for '{CurrentLabel}'");
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
    /// Resets the PCA model, optionally preserving captured clusters.
    /// </summary>
    /// <remarks>
    /// Behaviour depends on <see cref="PreserveClustersOnReset"/>:
    /// <list type="bullet">
    ///     <item><description>If <see langword="true"/> (default): PCA parameters are reset; clusters are preserved.</description></item>
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
            Console.WriteLine($"[SupervisedPCA '{Name}'] Clusters cleared on reset");
        }
        else
        {
            Console.WriteLine($"[SupervisedPCA '{Name}'] Clusters preserved on reset");
        }
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
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
            JsonElement je when je.ValueKind == JsonValueKind.String
                              && int.TryParse(je.GetString(), out var v) => v,
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
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble(),
            JsonElement je when je.ValueKind == JsonValueKind.String
                              && double.TryParse(je.GetString(), NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out var v) => v,
            _ => def
        };
    }

    #endregion
}