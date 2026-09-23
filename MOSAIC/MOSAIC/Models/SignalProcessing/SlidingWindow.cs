using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Visualization;
using static MOSAIC.Components.Basics.JsonModel;

namespace MOSAIC.Models.SignalProcessing;

/// <summary>
/// Sliding window over a stream of row samples, publishing a windowed
/// <see cref="Matrix{T}"/> of <c>BufferSize × Channels</c> every <see cref="Stride"/> rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overview:</b> Incoming data (vectors or matrix batches) is appended row by row into
/// an internal buffer. When the buffer reaches <see cref="BufferSize"/> rows, a windowed
/// matrix is published downstream and the buffer is advanced by <see cref="Stride"/> rows,
/// producing overlap of <c>BufferSize − Stride</c> rows between consecutive windows.
/// </para>
/// <para>
/// <b>Zero pre-fill:</b> On first data arrival the buffer is pre-filled with
/// <c>BufferSize − 1</c> zero vectors so that the first window publishes immediately.
/// This ensures multiple sliding windows with different sizes start outputting at the
/// same time rather than waiting for their respective buffers to fill.
/// </para>
/// <para>
/// <b>Rate propagation:</b> The output rate is automatically calculated as
/// incoming rows per second divided by <see cref="Stride"/> and propagated when it changes. The local
/// <see cref="BlockVisualization"/> scope is informed of the upstream sample rate
/// (not the output rate) so its time-axis and y-axis match the data being fed.
/// </para>
/// <para>
/// <b>Window functions:</b> Rectangular (no weighting), Hamming, or Hann.
/// Each sample row is element-wise multiplied by the window coefficients before output.
/// </para>
/// <para>
/// <b>Input types:</b> Accepts <see cref="Vector{T}"/> (single row),
/// <see cref="Matrix{T}"/> (batch of rows), or tagged tuples wrapping either.
/// </para>
/// <para>
/// <b>Thread safety:</b> All buffer access is synchronised via a <see cref="Lock"/>.
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs every published windowed matrix via <see cref="BaseBlock.Publish"/>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "Window256": {
///     "Type": "SlidingWindow",
///     "Inputs": [ "EMG" ],
///     "DesiredRate": 2000,
///     "Params": [ 256, 128, "Hamming" ],
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description><c>BufferSize</c> (int) — number of samples in the window.</description></item>
///   <item><term>1</term><description><c>Stride</c> (int) — number of samples between consecutive windows.</description></item>
///   <item><term>2</term><description><c>WindowType</c> (string) — Rectangular, Hamming, or Hann.</description></item>
/// </list>
/// </para>
/// </example>
public partial class SlidingWindow : BaseBlock
{
    /// <summary>Window function type.</summary>
    public enum WindowType { Rectangular, Hamming, Hann }

    public const int DefaultBufferSize = 100;
    public const int DefaultStride = 25;
    public const WindowType DefaultWindow = WindowType.Hamming;
    protected override bool TransformsPublicationRate => true;
    private bool _receivesVectors;

    /// <summary>Visualization helper for binding the latest input to the UI scope.</summary>
    public BlockVisualization Viz { get; set; } = new();

    /// <inheritdoc />
    protected override BlockVisualization? Visualization => Viz;

    /// <summary>Number of samples in the window buffer.</summary>
    [ObservableProperty] private int _bufferSize;

    /// <summary>Number of samples between consecutive window outputs (overlap = BufferSize − Stride).</summary>
    [ObservableProperty] private int _stride;

    /// <summary>Window function applied to each output matrix.</summary>
    [ObservableProperty] private WindowType _windowKind;

    /// <summary>Calculated output rate (incoming rows per second divided by stride).</summary>
    public double OutputRate => RowRate > 0 && Stride > 0 ? RowRate / Stride : 0;

    /// <summary>Overlap percentage between consecutive windows.</summary>
    public int OverlapPercent => BufferSize > 0 && Stride > 0
        ? 100 - (Stride * 100 / BufferSize)
        : 0;

    /// <summary>Human-readable configuration summary.</summary>
    public string ConfigSummary => BufferSize > 0 && Stride > 0
        ? $"{BufferSize} samples, stride {Stride} ({OverlapPercent}% overlap)"
        : "Not configured";

    /// <summary>Effective input rate: upstream-propagated or stored from constructor.</summary>
    private double EffectiveInputRate => InputRate > 0 ? InputRate : _storedInputRate;

    // Vectors contain one sample per publication. Matrices contain contiguous sample rows.
    // SignalRate describes spacing within a packet; it is not the cadence of feature vectors.
    private double RowRate => !_receivesVectors && SignalRate > 0 ? SignalRate : EffectiveInputRate;

    /// <summary>Accumulated rows waiting to form a complete window.</summary>
    private double[,]? _rows;
    private int _head;
    private int _count;

    /// <summary>Precomputed window coefficients.</summary>
    private Vector<double>? _window;

    /// <summary>Number of channels (columns), set from the first received row.</summary>
    private int _channels;

    /// <summary>Lock guarding all buffer access.</summary>
    private readonly object _gate = new();

    /// <summary>Stored input rate for use before upstream rate propagation arrives.</summary>
    private double _storedInputRate;

    /// <summary>
    /// Initializes a new <see cref="SlidingWindow"/> block.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="bufferSize">Window size in samples. Must be positive.</param>
    /// <param name="stride">Stride in samples. Must be in [1, bufferSize].</param>
    /// <param name="window">Window function type.</param>
    /// <param name="desiredRate">
    /// Incoming sample rate in Hz. Used to compute the initial output rate
    /// (<c>desiredRate / stride</c>) for downstream propagation.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="bufferSize"/> ≤ 0 or <paramref name="stride"/> is out of range.
    /// </exception>
    public SlidingWindow(string name,
                         int bufferSize = DefaultBufferSize,
                         int stride = DefaultStride,
                         WindowType window = DefaultWindow,
                         double desiredRate = 0) : base(name)
    {
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        if (stride <= 0 || stride > bufferSize)
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride must be > 0 and <= BufferSize.");

        _bufferSize = bufferSize;
        _stride = stride;
        _windowKind = window;

        if (desiredRate > 0)
        {
            _storedInputRate = desiredRate;
            DesiredRate = desiredRate / stride;

            // Inform the local viz scope of the upstream sample rate so it sizes
            // its buffer and time-axis to the actual data rate, not the 2000-point
            // default. Without this the y-axis settles on stale/partial data.
            Viz?.UpdateSignalRate(desiredRate);
        }
    }

    /// <summary>Rebuilds window, re-pads buffer with zeros, and keeps most recent rows when buffer size changes.</summary>
    partial void OnBufferSizeChanged(int value)
    {
        lock (_gate)
        {
            _window = BuildWindow(value, _windowKind);

            if (_channels > 0)
            {
                int keep = Math.Min(_count, value);
                var resized = new double[value, _channels];
                for (int r = 0; r < keep; r++)
                    for (int c = 0; c < _channels; c++)
                        resized[value - keep + r, c] = _rows![(_head + _count - keep + r) % _rows.GetLength(0), c];
                _rows = resized;
                _head = 0;
                _count = value;
            }
        }
        NotifyComputedPropertiesChanged();
        RecalculateOutputRate();
    }

    /// <summary>Recalculates output rate when stride changes.</summary>
    partial void OnStrideChanged(int value)
    {
        NotifyComputedPropertiesChanged();
        RecalculateOutputRate();
    }

    /// <summary>Rebuilds window coefficients when window type changes.</summary>
    partial void OnWindowKindChanged(WindowType value)
    {
        Debug.WriteLine($"[{Name}] WindowKind changed to: {value}");
        lock (_gate)
        {
            _window = BuildWindow(_bufferSize, value);
        }
        OnPropertyChanged(nameof(ConfigSummary));
    }

    /// <summary>Raises property-changed for all computed properties.</summary>
    private void NotifyComputedPropertiesChanged()
    {
        OnPropertyChanged(nameof(ConfigSummary));
        OnPropertyChanged(nameof(OutputRate));
        OnPropertyChanged(nameof(OverlapPercent));
    }

    /// <summary>Recalculates output rate when the upstream input rate changes.</summary>
    protected override void OnInputRateChanged(double newInputRate)
    {
        _storedInputRate = newInputRate;

        // Local viz shows pre-windowed input samples, so the scope's signal rate is the rate
        // rows arrive at (not the windowed output rate). This must be called every time the
        // upstream rate changes, otherwise the scope keeps its previous buffer size and the
        // y-axis ends up scanning the wrong span.
        Viz?.UpdateSignalRate(RowRate);

        RecalculateOutputRate();
    }

    /// <summary>
    /// Re-derives the rates once the source declares its true sample rate.
    /// </summary>
    /// <remarks>
    /// A batching source propagates <see cref="BaseBlock.SignalRate"/> separately from, and
    /// possibly later than, the publish rate that drives <see cref="OnInputRateChanged"/>. When it
    /// lands, <see cref="RowRate"/> changes from the publish rate to the real one, so the scope
    /// has to be told again — otherwise it keeps whichever value happened to arrive first.
    /// </remarks>
    protected override void OnSignalRateChanged(double newRate)
    {
        Viz?.UpdateSignalRate(RowRate);
        RecalculateOutputRate();
    }

    /// <summary>Computes output rate as incoming rows per second divided by stride and propagates downstream.</summary>
    private void RecalculateOutputRate()
    {
        double inputRate = RowRate;

        if (inputRate > 0 && Stride > 0)
        {
            double newOutputRate = inputRate / Stride;
            Debug.WriteLine($"[{Name}] Output rate: {inputRate:F0} Hz / {Stride} stride = {newOutputRate:F1} Hz");
            // True sample rate inside each window — force past stale downstream latches (e.g. an
            // FFT still on the old rate). Must be RowRate, not the stored publish rate: behind a
            // batching source those differ by the batch size, and pushing the publish rate here
            // would overwrite the real sample rate on every downstream consumer.
            UpdateAndPropagateSignalRate(RowRate);
            if (Math.Abs(DesiredRate - newOutputRate) >= 0.001)
                UpdateAndPropagateRate(newOutputRate);
        }
    }

    /// <summary>
    /// Reconfigures buffer size, stride, and window type at runtime.
    /// Triggers rate recalculation and downstream propagation.
    /// </summary>
    /// <param name="bufferSize">New window size. Must be positive.</param>
    /// <param name="stride">New stride. Must be in [1, bufferSize].</param>
    /// <param name="window">New window function type.</param>
    public void Reconfigure(int bufferSize, int stride, WindowType window)
    {
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        if (stride <= 0) throw new ArgumentOutOfRangeException(nameof(stride));
        if (stride > bufferSize) stride = bufferSize;

        lock (_gate)
        {
            BufferSize = bufferSize;
            Stride = stride;
            WindowKind = window;
        }
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_windowed.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_windowed";

    /// <summary>
    /// Creates a <see cref="SlidingWindow"/> from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">JSON model. See the class-level example for <c>Params</c> layout.</param>
    /// <returns>A configured <see cref="SlidingWindow"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown if required parameters are missing or invalid.</exception>
    public static SlidingWindow ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "SlidingWindow";
        var rate = m.DesiredRate ?? 0;
        var p = m.Params;

        var bufferSize = p is { Count: > 0 } ? GetInt(p[0], DefaultBufferSize) : DefaultBufferSize;
        var stride = p is { Count: > 1 } ? GetInt(p[1], DefaultStride) : DefaultStride;
        var wstr = p is { Count: > 2 } ? GetString(p[2], DefaultWindow.ToString()) ?? DefaultWindow.ToString() : DefaultWindow.ToString();

        if (bufferSize <= 0)
            throw new ArgumentException($"SlidingWindow '{name}': invalid bufferSize {bufferSize}.");
        if (stride <= 0)
            throw new ArgumentException($"SlidingWindow '{name}': invalid stride {stride}.");
        if (!Enum.TryParse<WindowType>(wstr, true, out var wtype) || !Enum.IsDefined(wtype))
            throw new ArgumentException($"SlidingWindow '{name}': invalid window type '{wstr}'. Use Rectangular|Hamming|Hann.");

        var block = ActivatorUtilities.CreateInstance<SlidingWindow>(sp, name, bufferSize, stride, wtype, rate);
        return block;
    }

    /// <inheritdoc />
    protected override string JsonTypeName => "SlidingWindow";

    /// <inheritdoc />
    protected override IReadOnlyList<object> GetJsonParams()
        => new List<object> { BufferSize, Stride, WindowKind.ToString() };

    /// <summary>
    /// Processes incoming data: appends rows to the buffer and publishes windowed matrices
    /// when the buffer is full. On first arrival, the buffer is pre-filled with zeros so
    /// the first window publishes immediately.
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="data">
    /// A <see cref="Vector{T}"/>, <see cref="Matrix{T}"/>, or tagged tuple wrapping either.
    /// </param>
    protected override void OnReceive(object sender, object data)
    {
        lock (_gate)
        {
            var payload = data is ValueTuple<string, object> tagged ? tagged.Item2 : data;
            if (payload is not Vector<double> && payload is not Matrix<double>) return;
            bool receivesVectors = payload is Vector<double>;
            if (_receivesVectors != receivesVectors)
            {
                _receivesVectors = receivesVectors;
                RecalculateOutputRate();
                Viz.UpdateSignalRate(RowRate);
            }
            if (payload is Vector<double> vector)
            {
                AppendRow(vector, null, 0, vector.Count);
                Viz.Feed(vector);
            }
            else if (payload is Matrix<double> matrix)
            {
                for (int r = 0; r < matrix.RowCount; r++)
                    AppendRow(null, matrix, r, matrix.ColumnCount);
                Viz.Feed(matrix);
            }
        }
    }

    // Copy directly into a fixed-size ring. Matrix input needs no temporary row vectors.
    private void AppendRow(Vector<double>? vector, Matrix<double>? matrix, int row, int channels)
    {
        if (_channels == 0)
        {
            if (channels == 0) return;
            _channels = channels;
            int capacity = BufferSize;
            _rows = new double[capacity, channels];
            _count = capacity - 1; // Preserve the established zero-padded first window.
            _window ??= BuildWindow(capacity, WindowKind);
        }
        if (channels != _channels) return;
        // Resizing can leave a complete pending window; preserve it before appending.
        PublishReadyWindow();
        // Observable settings can change before their change hook acquires _gate. Use the
        // installed buffer's size until that hook has resized it under the same lock.
        int index = (_head + _count) % _rows!.GetLength(0);
        for (int c = 0; c < channels; c++)
            _rows![index, c] = vector is not null ? vector[c] : matrix![row, c];
        _count++;
        PublishReadyWindow();
    }

    private void PublishReadyWindow()
    {
        int capacity = _rows!.GetLength(0);
        if (_count < capacity) return;
        Publish(BuildWindowedMatrix(capacity));
        int advance = Math.Min(Stride, _count);
        _head = (_head + advance) % capacity;
        _count -= advance;
    }

    /// <summary>Allocates only the stable matrix that is published downstream.</summary>
    private Matrix<double> BuildWindowedMatrix(int bufferSize)
    {
        var output = DenseMatrix.Create(bufferSize, _channels, 0.0);
        if (_window is null || _window.Count != bufferSize)
            _window = BuildWindow(bufferSize, WindowKind);
        for (int r = 0; r < bufferSize; r++)
        {
            int index = (_head + r) % bufferSize;
            double weight = _window[r];
            for (int c = 0; c < _channels; c++)
                output[r, c] = _rows![index, c] * weight;
        }
        return output;
    }

    /// <summary>
    /// Computes window function coefficients for the given size and type.
    /// </summary>
    private static Vector<double> BuildWindow(int n, WindowType wt) => wt switch
    {
        WindowType.Hamming => DenseVector.Create(n, r =>
            0.54 - 0.46 * Math.Cos(2.0 * Math.PI * r / Math.Max(1, n - 1))),

        WindowType.Hann => DenseVector.Create(n, r =>
            0.5 * (1.0 - Math.Cos(2.0 * Math.PI * r / Math.Max(1, n - 1)))),

        _ => DenseVector.Create(n, 1.0)
    };

    /// <summary>Disposes visualization and base class resources.</summary>
    public override void Dispose()
    {
        Viz?.Dispose();
        base.Dispose();
    }
}
