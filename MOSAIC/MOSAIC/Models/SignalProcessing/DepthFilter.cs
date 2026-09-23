using System;
using System.Collections.Generic;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Components.SignalProcessing;
using MOSAIC.Models.SignalProcessing;
using MOSAIC.Visualization.Heatmap;
using MOSAIC.Visualization.SnapshotMonitor;
using static MOSAIC.Components.Basics.JsonModel;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.Models.SignalProcessing;

/// <summary>
/// Digital filter block that applies filtering along the depth (samples) dimension of each frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>Key difference from <see cref="Filter"/>:</b> The <see cref="Filter"/> block filters
/// across <em>time</em> (sample-by-sample, maintaining state between frames). This block
/// filters along the <em>depth</em> axis within each frame — filter state is reset per frame,
/// so each frame is processed independently.
/// </para>
/// <para>
/// Designed for A-mode ultrasound lines or similar depth signals. Expects either a
/// <see cref="Vector"/> of length <c>nSamples</c> or a <see cref="Matrix"/> of shape
/// <c>(nChannels × nSamples)</c>. The filter is applied independently to each row (channel)
/// along the column (depth/sample) axis.
/// </para>
/// <para>
/// Uses the same <see cref="FilterDesign"/> factory and <see cref="IOnlineFilter"/> infrastructure
/// as the time-domain <see cref="Filter"/> block. Filter parameters (type, cutoffs, order, taps)
/// are fully adjustable at runtime via the UI.
/// </para>
/// <para>
/// <b>Sample rate convention:</b> For depth filtering, the "sample rate" represents the
/// spatial sampling frequency of the depth signal (e.g., samples per unit distance).
/// This is set via <see cref="DepthSampleRate"/> and defaults to 1000 if unset.
/// </para>
/// <para>
/// <b>JSON configuration:</b>
/// <code>
/// "depthLP": {
///   "Type": "DepthFilter",
///   "Inputs": ["wulpusPy"],
///   "Params": ["Lowpass", "IIR", 50, 150, 4, 64, 1000]
/// }
/// </code>
/// Params: [FilterType, Implementation, CutoffLow, CutoffHigh, Order, FirTaps, DepthSampleRate]
/// </para>
/// </remarks>
public sealed partial class DepthFilter : BaseBlock
{
    #region Fields

    private readonly object _filterLock = new();

    /// <summary>Cached filter instance, rebuilt only on <see cref="ApplyFilter"/>.</summary>
    private IOnlineFilter? _cachedFilter;

    #endregion

    #region Observable Properties

    /// <summary>Filter type (Lowpass, Highpass, Bandpass, Bandstop).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterDescription))]
    private FilterType _filterType = FilterType.Lowpass;

    /// <summary>Filter implementation (FIR or IIR).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterDescription))]
    private FilterImplementation _implementation = FilterImplementation.IIR;

    /// <summary>Lower cutoff frequency in Hz (or spatial frequency units).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterDescription))]
    private double _cutoffLow = 50;

    /// <summary>Upper cutoff frequency (used for Bandpass/Bandstop).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterDescription))]
    private double _cutoffHigh = 150;

    /// <summary>IIR filter order (Butterworth).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterDescription))]
    private int _order = 4;

    /// <summary>FIR filter tap count.</summary>
    [ObservableProperty]
    private int _firTaps = 64;

    /// <summary>
    /// Spatial sample rate for the depth dimension.
    /// For ultrasound this represents the sampling frequency along the depth axis.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterDescription))]
    private double _depthSampleRate = 1000;

    /// <summary>Whether filtering is active. When <see langword="false"/>, data passes through unchanged.</summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>Number of channels detected from the last input.</summary>
    [ObservableProperty] private int _channelCount;

    /// <summary>Total frames processed since creation.</summary>
    [ObservableProperty] private long _framesProcessed;

    /// <summary>
    /// <see langword="true"/> when UI parameters differ from the active filter.
    /// Bound to a visual indicator so the user knows to press Apply.
    /// </summary>
    [ObservableProperty] private bool _needsApply;

    #endregion

    #region Parameter Change Tracking

    partial void OnFilterTypeChanged(FilterType value) => NeedsApply = true;
    partial void OnImplementationChanged(FilterImplementation value) => NeedsApply = true;
    partial void OnCutoffLowChanged(double value) => NeedsApply = true;
    partial void OnCutoffHighChanged(double value) => NeedsApply = true;
    partial void OnOrderChanged(int value) => NeedsApply = true;
    partial void OnFirTapsChanged(int value) => NeedsApply = true;
    partial void OnDepthSampleRateChanged(double value) => NeedsApply = true;

    #endregion

    #region Computed Properties

    /// <summary>Human-readable filter description for UI display.</summary>
    public string FilterDescription
    {
        get
        {
            var implStr = Implementation == FilterImplementation.FIR ? $"FIR({FirTaps})" : $"IIR({Order})";
            return FilterType switch
            {
                FilterType.Lowpass or FilterType.Highpass => $"{FilterType} {implStr} Fc={CutoffLow}",
                FilterType.Bandpass or FilterType.Bandstop => $"{FilterType} {implStr} {CutoffLow}-{CutoffHigh}",
                _ => $"{FilterType} {implStr}"
            };
        }
    }

    #endregion

    #region Public Surface

    /// <summary>Standalone heatmap for B-mode display (configs × samples grid).</summary>
    public HeatMapMonitor Heatmap { get; } = new();

    /// <summary>Standalone A-mode snapshot monitor (spatial waveform, multi-trace).</summary>
    public SnapshotMonitor Snapshot { get; } = new();

    #endregion

    #region Constructor & Factory

    /// <summary>
    /// Creates a new <see cref="DepthFilter"/> block.
    /// </summary>
    public DepthFilter(
        string name,
        double desiredRate = 0,
        FilterType filterType = FilterType.Lowpass,
        FilterImplementation implementation = FilterImplementation.IIR,
        double cutoffLow = 50,
        double cutoffHigh = 150,
        int order = 4,
        int firTaps = 64,
        double depthSampleRate = 1000)
        : base(name, desiredRate)
    {
        _filterType = filterType;
        _implementation = implementation;
        _cutoffLow = cutoffLow;
        _cutoffHigh = cutoffHigh;
        _order = order;
        _firTaps = firTaps;
        _depthSampleRate = depthSampleRate;

        Heatmap.UseGlobalAuto();
        ApplyFilter();
        Debug.WriteLine($"[{Name}] Initialized: {FilterDescription}, DepthFs={DepthSampleRate}");
    }

    /// <summary>
    /// Creates a <see cref="DepthFilter"/> from JSON pipeline configuration.
    /// </summary>
    public static DepthFilter ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "DepthFilter";
        var rate = m.DesiredRate ?? 0;

        var filterType = FilterType.Lowpass;
        var implementation = FilterImplementation.IIR;
        double cutoffLow = 50, cutoffHigh = 150;
        int order = 4, firTaps = 64;
        double depthSampleRate = 1000;

        if (m.Params is { Count: > 0 })
        {
            var typeStr = GetString(m.Params[0], "Lowpass");
            Enum.TryParse(typeStr, ignoreCase: true, out filterType);

            if (m.Params.Count > 1)
            {
                var implStr = GetString(m.Params[1], "IIR");
                Enum.TryParse(implStr, ignoreCase: true, out implementation);
            }
            if (m.Params.Count > 2) cutoffLow = GetDouble(m.Params[2], 50);
            if (m.Params.Count > 3) cutoffHigh = GetDouble(m.Params[3], 150);
            if (m.Params.Count > 4) order = GetInt(m.Params[4], 4);
            if (m.Params.Count > 5) firTaps = GetInt(m.Params[5], 64);
            if (m.Params.Count > 6) depthSampleRate = GetDouble(m.Params[6], 1000);
        }

        var block = ActivatorUtilities.CreateInstance<DepthFilter>(
            sp, name, rate, filterType, implementation, cutoffLow, cutoffHigh, order, firTaps, depthSampleRate);

        return block;
    }

    #endregion

    #region JSON Export

    /// <inheritdoc/>
    protected override string JsonTypeName => "DepthFilter";

    /// <inheritdoc/>
    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object>
        {
            FilterType.ToString(),
            Implementation.ToString(),
            CutoffLow,
            CutoffHigh,
            Order,
            FirTaps,
            DepthSampleRate
        };

    #endregion

    #region Data Processing

    /// <summary>
    /// Receives upstream data (Vector or Matrix) and applies the depth filter to each frame.
    /// </summary>
    protected override void OnReceive(object sender, object data)
    {
        if (!IsEnabled)
        {
            Publish(data);
            return;
        }

        switch (data)
        {
            case Vector v:
            {
                var filtered = FilterDepthVector(v);
                FramesProcessed++;
                Publish(filtered);
                Heatmap.EnqueueFrame(filtered);
                Snapshot.EnqueueSnapshot(filtered);
                break;
            }

            case Matrix m:
            {
                ChannelCount = m.RowCount;
                var result = Matrix.Build.Dense(m.RowCount, m.ColumnCount);

                for (int i = 0; i < m.RowCount; i++)
                    result.SetRow(i, FilterDepthVector(m.Row(i)));

                FramesProcessed++;
                Publish(result);

                // Heatmap: auto-configure grid, skip frame on dimension change
                if (Heatmap.Rows != result.RowCount || Heatmap.Columns != result.ColumnCount)
                    Heatmap.ConfigureGrid(result.RowCount, result.ColumnCount);
                else
                    Heatmap.EnqueueFrame(result.ToRowMajorArray().AsSpan());

                // Snapshot: all configs as multi-trace
                Snapshot.EnqueueSnapshot(result);
                break;
            }

            default:
                ReportError("Expected a vector or matrix of depth samples.");
                break;
        }
    }

    /// <summary>
    /// Rebuilds the cached filter from current UI parameters.
    /// Call from the ViewModel's Apply button.
    /// </summary>
    public void ApplyFilter()
    {
        lock (_filterLock)
        {
            try
            {
                var fs = DepthSampleRate > 0 ? DepthSampleRate : 1000;

                _cachedFilter = Implementation == FilterImplementation.FIR
                    ? CreateFirFilter(fs)
                    : CreateIirFilter(fs);

                NeedsApply = false;
                Debug.WriteLine($"[{Name}] Filter applied: {FilterDescription}, Fs={fs}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{Name}] Filter creation failed: {ex.Message}, using passthrough");
                _cachedFilter = new PassthroughFilter();
            }
        }
    }

    /// <summary>
    /// Filters a single depth vector using the cached filter.
    /// The filter state is reset before each vector so depth frames are independent.
    /// </summary>
    private Vector FilterDepthVector(Vector x)
    {
        lock (_filterLock)
        {
            if (_cachedFilter is null)
            {
                ApplyFilter();
                if (_cachedFilter is null) return x;
            }

            _cachedFilter.Reset();
            var samples = x.ToArray();
            var filtered = _cachedFilter.ProcessSamples(samples);
            return Vector.Build.DenseOfArray(filtered);
        }
    }

    private IOnlineFilter CreateFirFilter(double fs) => FilterType switch
    {
        FilterType.Lowpass  => FilterDesign.CreateFirLowpass(fs, CutoffLow, FirTaps),
        FilterType.Highpass => FilterDesign.CreateFirHighpass(fs, CutoffLow, FirTaps),
        FilterType.Bandpass => FilterDesign.CreateFirBandpass(fs, CutoffLow, CutoffHigh, FirTaps),
        FilterType.Bandstop => FilterDesign.CreateFirBandstop(fs, CutoffLow, CutoffHigh, FirTaps),
        _ => new PassthroughFilter()
    };

    private IOnlineFilter CreateIirFilter(double fs) => FilterType switch
    {
        FilterType.Lowpass  => FilterDesign.CreateIirLowpass(fs, CutoffLow, Order),
        FilterType.Highpass => FilterDesign.CreateIirHighpass(fs, CutoffLow, Order),
        FilterType.Bandpass => FilterDesign.CreateIirBandpass(fs, CutoffLow, CutoffHigh, Order),
        FilterType.Bandstop => FilterDesign.CreateIirBandstop(fs, CutoffLow, CutoffHigh, Order),
        _ => new PassthroughFilter()
    };

    #endregion

    #region Dispose

    /// <inheritdoc/>
    public override void Dispose()
    {
        Heatmap.Dispose();
        Snapshot.Dispose();
        base.Dispose();
    }

    #endregion
}