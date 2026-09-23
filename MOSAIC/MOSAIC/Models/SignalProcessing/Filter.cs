using System;
using System.Collections.Generic;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Components.SignalProcessing;
using MOSAIC.Visualization;
using static MOSAIC.Components.Basics.JsonModel;

namespace MOSAIC.Models.SignalProcessing;

/// <summary>Filter type enumeration.</summary>
public enum FilterType { Lowpass, Highpass, Bandpass, Bandstop }

/// <summary>Filter implementation type.</summary>
public enum FilterImplementation
{
    /// <summary>Finite Impulse Response — always stable, linear phase.</summary>
    FIR,

    /// <summary>Infinite Impulse Response (Butterworth) — sharper cutoff, less delay.</summary>
    IIR
}

/// <summary>
/// Dynamic digital filter block with real-time parameter adjustment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> Applies a per-channel IIR or FIR filter to incoming vectors.
/// Filter parameters (type, cutoff, order) can be changed at runtime — the internal filter
/// bank is automatically rebuilt on the next sample after any parameter change.
/// </para>
/// <para>
/// <b>Inputs:</b> Accepts <see cref="Vector{T}"/> of <see cref="double"/>,
/// <see cref="double"/>[], or scalar <see cref="double"/>. Each channel is filtered
/// independently with its own filter instance.
/// </para>
/// <para>
/// <b>Filter types:</b> Lowpass, Highpass, Bandpass, Bandstop.
/// <b>Implementations:</b> IIR (Butterworth, configurable order) or FIR (configurable tap count).
/// </para>
/// <para>
/// <b>Bypass:</b> When <see cref="IsEnabled"/> is <see langword="false"/>, input data is
/// forwarded unchanged (passthrough mode).
/// </para>
/// <para>
/// <b>Sample rate:</b> Determined from <see cref="BaseBlock.DesiredRate"/> (typically inherited
/// from the upstream block via rate propagation). Falls back to 1000 Hz if unset.
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs every published value via <see cref="BaseBlock.Publish"/>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "LP50": {
///     "Type": "FilterBlock",
///     "Inputs": [ "EMG" ],
///     "Params": [ "Lowpass", "IIR", 50, 150, 4, 64 ],
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description><c>FilterType</c> (string) — Lowpass, Highpass, Bandpass, Bandstop.</description></item>
///   <item><term>1</term><description><c>Implementation</c> (string, default <c>"IIR"</c>) — FIR or IIR.</description></item>
///   <item><term>2</term><description><c>CutoffLow</c> (double, default 50) — lower cutoff frequency in Hz.</description></item>
///   <item><term>3</term><description><c>CutoffHigh</c> (double, default 150) — upper cutoff for Bandpass/Bandstop.</description></item>
///   <item><term>4</term><description><c>Order</c> (int, default 4) — IIR filter order.</description></item>
///   <item><term>5</term><description><c>FirTaps</c> (int, default 64) — FIR filter length.</description></item>
/// </list>
/// </para>
/// </example>
public sealed partial class Filter : BaseBlock
{
    /// <summary>Per-channel filter instances, one per input channel.</summary>
    private IOnlineFilter[]? _filters;

    /// <summary>Lock guarding <see cref="_filters"/> for concurrent parameter changes.</summary>
    private readonly object _filterLock = new();

    /// <summary>Number of channels (set from first input vector dimension).</summary>
    private int _channelCount;

    /// <summary>Flag indicating filters need to be rebuilt before the next sample.</summary>
    private bool _needsRebuild = true;

    /// <summary>Filter type (Lowpass, Highpass, Bandpass, Bandstop).</summary>
    private FilterType _filterType = FilterType.Lowpass;
    public FilterType FilterType
    {
        get { lock (_filterLock) return _filterType; }
        set => UpdateParameters(filterType: value);
    }

    /// <summary>Filter implementation (FIR or IIR).</summary>
    private FilterImplementation _implementation = FilterImplementation.IIR;
    public FilterImplementation Implementation
    {
        get { lock (_filterLock) return _implementation; }
        set => UpdateParameters(implementation: value);
    }

    /// <summary>Lower cutoff frequency in Hz.</summary>
    private double _cutoffLow = 50;
    public double CutoffLow
    {
        get { lock (_filterLock) return _cutoffLow; }
        set => UpdateParameters(cutoffLow: value);
    }

    /// <summary>Upper cutoff frequency in Hz (used for Bandpass/Bandstop).</summary>
    private double _cutoffHigh = 150;
    public double CutoffHigh
    {
        get { lock (_filterLock) return _cutoffHigh; }
        set => UpdateParameters(cutoffHigh: value);
    }

    /// <summary>IIR filter order (Butterworth).</summary>
    private int _order = 4;
    public int Order
    {
        get { lock (_filterLock) return _order; }
        set => UpdateParameters(order: value);
    }

    /// <summary>FIR filter tap count.</summary>
    private int _firTaps = 64;
    public int FirTaps
    {
        get { lock (_filterLock) return _firTaps; }
        set => UpdateParameters(firTaps: value);
    }

    /// <summary>Whether filtering is active. When <see langword="false"/>, data passes through unchanged.</summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>Total samples processed since creation.</summary>
    [ObservableProperty]
    private long _samplesProcessed;
    
    public BlockVisualization Viz { get; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;


    /// <summary>
    /// Sample rate for filter design. Inherited from <see cref="BaseBlock.DesiredRate"/>;
    /// falls back to 1000 Hz if unset.
    /// </summary>
    public double SampleRate => SignalRate > 0 ? SignalRate 
        : DesiredRate > 0 ? DesiredRate 
        : 1000;


    /// <summary>Human-readable filter description for UI display.</summary>
    public string FilterDescription => GetFilterDescription();

    /// <summary>Fired when filter parameters change (type, implementation, cutoffs, order).</summary>
    public event Action<FilterType, FilterImplementation, double, double, int>? OnParametersChanged;

    /// <summary>Fired when filtered data is published (for ViewModel subscription).</summary>
    public event Action<object>? OnPublish;

    /// <summary>
    /// Initializes a new <see cref="Filter"/> block.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="desiredRate">Sample rate in Hz (typically inherited from upstream).</param>
    /// <param name="filterType">Initial filter type.</param>
    /// <param name="implementation">Initial implementation (FIR or IIR).</param>
    /// <param name="cutoffLow">Lower cutoff frequency in Hz.</param>
    /// <param name="cutoffHigh">Upper cutoff frequency in Hz.</param>
    /// <param name="order">IIR filter order.</param>
    /// <param name="firTaps">FIR tap count.</param>
    public Filter(
        string name,
        double desiredRate = 0,
        FilterType filterType = FilterType.Lowpass,
        FilterImplementation implementation = FilterImplementation.IIR,
        double cutoffLow = 50,
        double cutoffHigh = 150,
        int order = 4,
        int firTaps = 64)
        : base(name, desiredRate)
    {
        UpdateParameters(filterType, implementation, cutoffLow, cutoffHigh, order, firTaps);

        Debug.WriteLine($"[{Name}] Initialized: {GetFilterDescription()}");
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_filtered.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_filtered";

    /// <summary>
    /// Creates a <see cref="Filter"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// JSON model. See the class-level example for <c>Params</c> layout.
    /// </param>
    /// <returns>A configured <see cref="Filter"/> instance.</returns>
    public static Filter ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "Filter";
        var rate = m.DesiredRate ?? 0;

        var filterType = FilterType.Lowpass;
        var implementation = FilterImplementation.IIR;
        double cutoffLow = 50;
        double cutoffHigh = 150;
        int order = 4;
        int firTaps = 64;

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
        }

        Debug.WriteLine($"[{name}] Config: {filterType} {implementation}, " +
                        $"Fs={( rate > 0 ? $"{rate}" : "inherit")}, Fc={cutoffLow}/{cutoffHigh}, Order={order}");

        var block = ActivatorUtilities.CreateInstance<Filter>(
            sp, name, rate, filterType, implementation, cutoffLow, cutoffHigh, order, firTaps);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "FilterBlock";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object>
        {
            FilterType.ToString(),
            Implementation.ToString(),
            CutoffLow,
            CutoffHigh,
            Order,
            FirTaps
        };

    #endregion

    protected override void OnSignalRateChanged(double newRate)
    {
        InvalidateFilters();
        OnPropertyChanged(nameof(SampleRate));
    }

    /// <summary>Rebuilds filters when the sample rate changes.</summary>
    protected override void OnDesiredRateChanged(double oldRate, double newRate)
    {
        Debug.WriteLine($"[{Name}] Sample rate changed: {oldRate:F0} → {newRate:F0} Hz");

        lock (_filterLock) { _needsRebuild = true; }



        OnPropertyChanged(nameof(SampleRate));
        OnPropertyChanged(nameof(FilterDescription));
    }

    /// <summary>
    /// Processes incoming data. When disabled, passes through unchanged.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="data">
    /// A <see cref="Vector{T}"/>, <see cref="double"/>[], or scalar <see cref="double"/>.
    /// </param>
    protected override void OnReceive(object sender, object data)
    {
        if (!IsEnabled)
        {
            Publish(data);
            OnPublish?.Invoke(data);
            return;
        }

        try
        {
            if (data is Vector<double> vec)
            {
                var filtered = ProcessVector(vec);
                SamplesProcessed++;
                Publish(filtered);
                OnPublish?.Invoke(filtered);
                Viz.Feed(filtered);
            }
            else if (data is double[] arr)
            {
                var filtered = ProcessVector(Vector<double>.Build.Dense(arr));
                SamplesProcessed++;
                Publish(filtered);
                OnPublish?.Invoke(filtered);
                Viz.Feed(filtered);
            }
            else if (data is double val)
            {
                var filtered = ProcessSingle(val, 0);
                SamplesProcessed++;
                Publish(filtered);
                OnPublish?.Invoke(filtered);
            }
            else if (data is Matrix<double> mat)
            {
                var filtered = ProcessMatrix(mat);
                SamplesProcessed += mat.RowCount;
                Publish(filtered);
                OnPublish?.Invoke(filtered);
                Viz.Feed(filtered);
            }
        }
        catch (Exception ex)
        {
            ReportError("Filtering failed; this input was not published.", ex);
        }
    }

    /// <summary>Filters a multi-channel vector, one filter per channel.</summary>
    private Vector<double> ProcessVector(Vector<double> input)
    {
        lock (_filterLock)
        {
            if (_needsRebuild || _filters == null || _filters.Length != input.Count)
                RebuildFilters(input.Count);

            var output = Vector<double>.Build.Dense(input.Count);
            for (int i = 0; i < input.Count; i++)
                output[i] = _filters![i].ProcessSample(input[i]);

            return output;
        }
    }
    
    /// <summary>
    /// Filters a batch of timesteps packed as a [timesteps × channels] matrix.
    /// Each row is one timestep; rows are fed through the per-channel filters
    /// in order so state carries correctly across the batch.
    /// </summary>
    private Matrix<double> ProcessMatrix(Matrix<double> input)
    {
        // input is [timesteps × channels]
        int timesteps = input.RowCount;
        int channels  = input.ColumnCount;

        lock (_filterLock)
        {
            if (_needsRebuild || _filters == null || _filters.Length != channels)
                RebuildFilters(channels);

            var output = Matrix<double>.Build.Dense(timesteps, channels);
            for (int t = 0; t < timesteps; t++)
            for (int ch = 0; ch < channels; ch++)
                output[t, ch] = _filters![ch].ProcessSample(input[t, ch]);

            return output;
        }
    }
    /// <summary>Filters a single scalar value on the specified channel.</summary>
    private double ProcessSingle(double input, int channel)
    {
        lock (_filterLock)
        {
            if (_needsRebuild || _filters == null || _filters.Length == 0)
                RebuildFilters(1);

            return _filters![channel].ProcessSample(input);
        }
    }

    /// <summary>Rebuilds the per-channel filter bank.</summary>
    private void RebuildFilters(int channels)
    {
        _channelCount = channels;
        _filters = new IOnlineFilter[channels];

        for (int i = 0; i < channels; i++)
            _filters[i] = CreateFilter();

        _needsRebuild = false;

        Debug.WriteLine($"[{Name}] Filters rebuilt: {channels} channels, {GetFilterDescription()}");
    }

    /// <summary>Creates a single filter instance based on current parameters.</summary>
    private IOnlineFilter CreateFilter() => Implementation == FilterImplementation.FIR
        ? CreateFirFilter() : CreateIirFilter();

    /// <summary>Creates a FIR filter based on current type and parameters.</summary>
    private IOnlineFilter CreateFirFilter() => FilterType switch
    {
        FilterType.Lowpass  => FilterDesign.CreateFirLowpass(SampleRate, CutoffLow, FirTaps),
        FilterType.Highpass => FilterDesign.CreateFirHighpass(SampleRate, CutoffLow, FirTaps),
        FilterType.Bandpass => FilterDesign.CreateFirBandpass(SampleRate, CutoffLow, CutoffHigh, FirTaps),
        FilterType.Bandstop => FilterDesign.CreateFirBandstop(SampleRate, CutoffLow, CutoffHigh, FirTaps),
        _ => throw new InvalidOperationException("Unknown filter type.")
    };

    /// <summary>Creates an IIR (Butterworth) filter based on current type and parameters.</summary>
    private IOnlineFilter CreateIirFilter() => FilterType switch
    {
        FilterType.Lowpass  => FilterDesign.CreateIirLowpass(SampleRate, CutoffLow, Order),
        FilterType.Highpass => FilterDesign.CreateIirHighpass(SampleRate, CutoffLow, Order),
        FilterType.Bandpass => FilterDesign.CreateIirBandpass(SampleRate, CutoffLow, CutoffHigh, Order),
        FilterType.Bandstop => FilterDesign.CreateIirBandstop(SampleRate, CutoffLow, CutoffHigh, Order),
        _ => throw new InvalidOperationException("Unknown filter type.")
    };

    /// <summary>Builds the human-readable filter description string.</summary>
    private string GetFilterDescription()
    {
        var implStr = Implementation == FilterImplementation.FIR ? $"FIR({FirTaps})" : $"IIR({Order})";
        return FilterType switch
        {
            FilterType.Lowpass or FilterType.Highpass => $"{FilterType} {implStr} Fc={CutoffLow}Hz",
            FilterType.Bandpass or FilterType.Bandstop => $"{FilterType} {implStr} {CutoffLow}-{CutoffHigh}Hz",
            _ => $"{FilterType} {implStr}"
        };
    }

    /// <summary>
    /// Updates filter parameters and triggers a rebuild.
    /// </summary>
    /// <param name="filterType">New filter type, or <see langword="null"/> to keep current.</param>
    /// <param name="implementation">New implementation, or <see langword="null"/> to keep current.</param>
    /// <param name="cutoffLow">New lower cutoff, or <see langword="null"/> to keep current.</param>
    /// <param name="cutoffHigh">New upper cutoff, or <see langword="null"/> to keep current.</param>
    /// <param name="order">New order, or <see langword="null"/> to keep current.</param>
    /// <param name="firTaps">New FIR taps, or <see langword="null"/> to keep current.</param>
    public void UpdateParameters(
        FilterType? filterType = null,
        FilterImplementation? implementation = null,
        double? cutoffLow = null,
        double? cutoffHigh = null,
        int? order = null,
        int? firTaps = null)
    {
        FilterType type;
        FilterImplementation impl;
        double low, high;
        int nextOrder, taps;
        lock (_filterLock)
        {
            type = filterType ?? _filterType;
            impl = implementation ?? _implementation;
            low = cutoffLow ?? _cutoffLow;
            high = cutoffHigh ?? _cutoffHigh;
            nextOrder = order ?? _order;
            taps = firTaps ?? _firTaps;
            if (!Enum.IsDefined(type) || !Enum.IsDefined(impl)) throw new ArgumentException("Unknown filter type or implementation.");
            if (!double.IsFinite(low) || low <= 0) throw new ArgumentOutOfRangeException(nameof(cutoffLow));
            if (!double.IsFinite(high) || high < 0) throw new ArgumentOutOfRangeException(nameof(cutoffHigh));
            if (type is FilterType.Bandpass or FilterType.Bandstop && high <= low)
                throw new ArgumentException("The upper band cutoff must exceed the lower cutoff.");
            if (nextOrder <= 0) throw new ArgumentOutOfRangeException(nameof(order));
            if (taps <= 0) throw new ArgumentOutOfRangeException(nameof(firTaps));
            _filterType = type;
            _implementation = impl;
            _cutoffLow = low;
            _cutoffHigh = high;
            _order = nextOrder;
            _firTaps = taps;
            _needsRebuild = true;
        }
        // Notify only after all values have been committed. No callback can see a half-applied band.
        foreach (var property in new[] { nameof(FilterType), nameof(Implementation), nameof(CutoffLow),
                     nameof(CutoffHigh), nameof(Order), nameof(FirTaps), nameof(FilterDescription) })
            OnPropertyChanged(property);
        OnParametersChanged?.Invoke(type, impl, low, high, nextOrder);
    }

    /// <summary>Sets the cutoff frequency (for Lowpass/Highpass).</summary>
    /// <param name="frequency">Cutoff frequency in Hz.</param>
    public void SetCutoff(double frequency) => UpdateParameters(cutoffLow: frequency);

    /// <summary>Sets the band frequencies (for Bandpass/Bandstop).</summary>
    /// <param name="lowFreq">Lower band edge in Hz.</param>
    /// <param name="highFreq">Upper band edge in Hz.</param>
    public void SetBand(double lowFreq, double highFreq) =>
        UpdateParameters(cutoffLow: lowFreq, cutoffHigh: highFreq);

    /// <summary>Resets all filter states (clears history) without changing parameters.</summary>
    public void ResetFilters()
    {
        lock (_filterLock)
        {
            if (_filters != null)
                foreach (var filter in _filters)
                    filter.Reset();
        }
        Debug.WriteLine($"[{Name}] Filters reset");
    }

    /// <summary>Forces a filter rebuild on the next sample.</summary>
    public void InvalidateFilters()
    {
        lock (_filterLock) { _needsRebuild = true; }
    }


}
