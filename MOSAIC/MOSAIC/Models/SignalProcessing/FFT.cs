using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FftFlat;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;
using MOSAIC.Visualization.SpectrogramMonitor;
using static MOSAIC.Components.Basics.JsonModel;

namespace MOSAIC.Models.SignalProcessing;

/// <summary>
/// Computes the FFT magnitude spectrum for each channel using FftFlat (MIT, Ooura).
/// </summary>
/// <remarks>
/// <para>
/// <b>Input:</b> A <see cref="Matrix{T}"/> (rows = samples, columns = channels) from an
/// upstream SlidingWindow block. Also accepts a single <see cref="Vector{T}"/> which is
/// treated as a single-channel column matrix.
/// </para>
/// <para>
/// <b>Output:</b> A <see cref="Matrix{T}"/> with <c>N/2+1</c> rows (frequency bins) and
/// one column per channel, scaled according to <see cref="Mode"/>.
/// </para>
/// <para>
/// <b>Output modes:</b>
/// <list type="bullet">
///   <item><description><see cref="OutputMode.Magnitude"/> — raw magnitude.</description></item>
///   <item><description><see cref="OutputMode.MagnitudeNormalized"/> — single-sided amplitude: magnitude × 2/N (interior bins) and magnitude × 1/N at the unpaired DC and Nyquist bins.</description></item>
///   <item><description><see cref="OutputMode.Power"/> — magnitude².</description></item>
///   <item><description><see cref="OutputMode.DB"/> — 20·log₁₀(magnitude / <see cref="DbRef"/>), floored at <see cref="DbFloor"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Window functions:</b> Rectangular (default), Hann, Hamming, Blackman. The upstream
/// SlidingWindow should typically apply its own window; this block defaults to Rectangular
/// to avoid double-windowing.
/// </para>
/// <para>
/// <b>Spectrogram:</b> A <see cref="SpectrogramMonitor"/> is created in the constructor for
/// real-time visualisation. <see cref="SelectedChannel"/>: −1 = average all channels,
/// 0..N−1 = single channel. The spectrogram always displays in dB regardless of
/// <see cref="Mode"/>.
/// </para>
/// <para>
/// <b>Sample rate:</b> The frequency axis follows the live <see cref="BaseBlock.SignalRate"/>
/// (the true sample rate, which survives windowing and tracks runtime pipeline-rate changes).
/// <c>Params[5]</c> is an optional fixed override, used only when the pipeline does not propagate
/// a SignalRate; the block never uses the window fire rate (sampleRate / stride).
/// </para>
/// <para>
/// <b>CSV logging:</b> When a <c>Path</c> is specified, <see cref="BaseBlock.Dumper"/>
/// automatically logs the published spectrum matrix via <see cref="BaseBlock.Publish"/>.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "Spectrum": {
///     "Type": "FFT",
///     "Inputs": [ "SlidingWindow1" ],
///     "Params": [ "Rectangular", "Magnitude", 1.0, -120, 200, 2000 ],
///     "Path": "C:/Data/session1"
///   }
/// }
/// </code>
/// <para>
/// <b>Params:</b>
/// <list type="table">
///   <listheader><term>Index</term><description>Description</description></listheader>
///   <item><term>0</term><description><c>WindowType</c> (string, default <c>"Rectangular"</c>) — Rectangular, Hann, Hamming, or Blackman.</description></item>
///   <item><term>1</term><description><c>OutputMode</c> (string, default <c>"Magnitude"</c>) — Magnitude, MagnitudeNormalized, Power, or DB.</description></item>
///   <item><term>2</term><description><c>DbRef</c> (double, default 1.0) — dB reference level.</description></item>
///   <item><term>3</term><description><c>DbFloor</c> (double, default −120.0) — minimum dB value.</description></item>
///   <item><term>4</term><description><c>SpectrogramDepth</c> (int, default 200) — number of time slices in the spectrogram.</description></item>
///   <item><term>5</term><description><c>SampleRate</c> (double, default 0) — fixed override for the frequency axis, used only when the pipeline supplies no SignalRate. 0 = follow the live pipeline SignalRate (recommended).</description></item>
/// </list>
/// </para>
/// </example>
public partial class FFT : BaseBlock
{
    /// <summary>Spectrum scaling mode.</summary>
    public enum OutputMode { Magnitude, Power, DB, MagnitudeNormalized }

    /// <summary>Window function applied before the FFT.</summary>
    public enum WindowType { Rectangular, Hann, Hamming, Blackman }

    private volatile OutputMode _outputMode;
    private volatile WindowType _windowType;
    private double _dbRef;
    private double _dbFloor;

    private int _fftSize;
    private int _channels;
    private int _outputBins;
    private bool _initialized;
    private bool _invalidConfigLogged;

    private FastFourierTransform[]? _fftEngines;
    private double[]? _windowCoeffs;
    private Complex[][]? _fftBuffers;
    private double[]? _spectrumScratch;

    private long _frameCount;

    /// <summary>
    /// Original signal sample rate in Hz. Overrides the inferred rate from the pipeline
    /// because the FFT block receives data at the window fire rate, not the original sample rate.
    /// </summary>
    private double _sampleRate;

    /// <summary>Number of output frequency bins (N/2 + 1).</summary>
    [ObservableProperty]
    private int _outputBinCount;

    /// <summary>Current FFT size in samples.</summary>
    [ObservableProperty]
    private int _currentFftSize;

    /// <summary>Frequency resolution in Hz (sample rate / FFT size).</summary>
    [ObservableProperty]
    private double _frequencyResolution;

    /// <summary>Number of input channels.</summary>
    [ObservableProperty]
    private int _channelCount;

    /// <summary>
    /// Which channel to show in the spectrogram. −1 = average all channels, 0..N−1 = single channel.
    /// </summary>
    [ObservableProperty]
    private int _selectedChannel = -1;

    /// <summary>Output scaling mode. Can be changed at runtime.</summary>
    public OutputMode Mode
    {
        get => _outputMode;
        set
        {
            if (_outputMode == value) return;
            _outputMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FftDescription));
        }
    }

    /// <summary>Window function. Can be changed at runtime — recomputes coefficients.</summary>
    public WindowType Window
    {
        get => _windowType;
        set
        {
            if (_windowType == value) return;
            _windowType = value;
            if (_fftSize > 0)
                _windowCoeffs = ComputeWindow(_windowType, _fftSize);
            OnPropertyChanged();
            OnPropertyChanged(nameof(FftDescription));
        }
    }

    /// <summary>dB reference level (must be positive). Read/written across threads, so use a volatile access.</summary>
    public double DbRef
    {
        get => Volatile.Read(ref _dbRef);
        set
        {
            if (value <= 0 || Math.Abs(Volatile.Read(ref _dbRef) - value) < double.Epsilon) return;
            Volatile.Write(ref _dbRef, value);
            OnPropertyChanged();
        }
    }

    /// <summary>dB floor (minimum dB value displayed/returned). Read/written across threads.</summary>
    public double DbFloor
    {
        get => Volatile.Read(ref _dbFloor);
        set
        {
            if (Math.Abs(Volatile.Read(ref _dbFloor) - value) < double.Epsilon) return;
            Volatile.Write(ref _dbFloor, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Human-readable description string for UI display.</summary>
    public string FftDescription =>
        _fftSize > 0
            ? $"{_windowType} · {_outputMode} · {_fftSize} pts · Δf {FrequencyResolution:F1} Hz"
            : $"{_windowType} · {_outputMode}";

    /// <summary>Frequency values for each output bin (Hz). <see langword="null"/> until initialised.</summary>
    public double[]? FrequencyAxis { get; private set; }

    /// <summary>
    /// Spectrogram monitor for real-time frequency-domain visualisation.
    /// Created in the constructor, always non-null.
    /// </summary>
    public SpectrogramMonitor Spectrogram { get; }

    /// <summary>
    /// Initializes a new <see cref="FFT"/> block.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="desiredRate">Processing rate (typically 0 — inherited from pipeline).</param>
    /// <param name="windowType">Window function to apply before the FFT.</param>
    /// <param name="outputMode">Spectrum scaling mode.</param>
    /// <param name="dbRef">dB reference level (positive).</param>
    /// <param name="dbFloor">Minimum dB value.</param>
    /// <param name="spectrogramDepth">Number of time slices retained in the spectrogram.</param>
    /// <param name="sampleRate">Original signal sample rate (0 = infer from pipeline).</param>
    public FFT(
        string name = "FFT",
        double desiredRate = 0,
        WindowType windowType = WindowType.Rectangular,
        OutputMode outputMode = OutputMode.Magnitude,
        double dbRef = 1.0,
        double dbFloor = -120.0,
        int spectrogramDepth = 200,
        double sampleRate = 0)
    {
        Name = name;
        DesiredRate = desiredRate;

        _windowType = windowType;
        _outputMode = outputMode;
        _dbRef = dbRef > 0 ? dbRef : 1.0;
        _dbFloor = dbFloor;
        _sampleRate = sampleRate;

        Spectrogram = new SpectrogramMonitor(Math.Max(50, spectrogramDepth));

        Debug.WriteLine($"[FFT '{Name}'] Init (window={windowType}, mode={outputMode}, sampleRate={sampleRate})");
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_fft.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_fft";

    /// <summary>
    /// Creates an <see cref="FFT"/> block from a JSON pipeline definition.
    /// </summary>
    /// <param name="sp">Service provider for dependency injection.</param>
    /// <param name="m">
    /// JSON model. See the class-level example for the <c>Params</c> layout.
    /// </param>
    /// <returns>A configured <see cref="FFT"/> instance.</returns>
    public static FFT ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var name = m.Name ?? "FFT";
        var p = m.Params;

        var windowType = WindowType.Rectangular;
        if (p?.Count > 0)
        {
            var wStr = p[0]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(wStr) && Enum.TryParse<WindowType>(wStr, true, out var wt))
                windowType = wt;
        }

        var outputMode = OutputMode.Magnitude;
        if (p?.Count > 1)
        {
            var mStr = p[1]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(mStr) && Enum.TryParse<OutputMode>(mStr, true, out var om))
                outputMode = om;
        }

        var dbRef   = p?.Count > 2 ? GetDouble(p[2], 1.0)    : 1.0;
        var dbFloor = p?.Count > 3 ? GetDouble(p[3], -120.0)  : -120.0;
        var depth   = p?.Count > 4 ? GetInt(p[4], 200)        : 200;
        var sampleRate = p?.Count > 5 ? GetDouble(p[5], 0)    : 0;

        var block = new FFT(name, 0, windowType, outputMode, dbRef, dbFloor, depth, sampleRate);
        block.SetInputsFromConfig(m);
        return block;
    }

    #region JSON Export

    /// <inheritdoc />
    protected override string JsonTypeName => "FFT";

    /// <inheritdoc />
    protected override IReadOnlyList<object>? GetJsonParams()
        => new List<object>
        {
            _windowType.ToString(),
            _outputMode.ToString(),
            _dbRef,
            _dbFloor,
            Spectrogram.MaxDepth,
            _sampleRate
        };

    #endregion

    /// <summary>
    /// Initialises FFT engines, buffers, and frequency axis for the given dimensions.
    /// Returns <see langword="false"/> (without throwing) for an invalid configuration —
    /// a non-power-of-2 size or non-positive dimensions — so a misconfigured pipeline
    /// degrades to "no output" instead of throwing once per frame.
    /// </summary>
    /// <param name="fftSize">Number of samples (must be a power of 2).</param>
    /// <param name="channels">Number of input channels.</param>
    /// <returns><see langword="true"/> if the block is ready to process; otherwise <see langword="false"/>.</returns>
    private bool EnsureInit(int fftSize, int channels)
    {
        if (_initialized && _fftSize == fftSize && _channels == channels)
            return true;

        if (fftSize <= 0 || channels <= 0 || (fftSize & (fftSize - 1)) != 0)
        {
            if (!_invalidConfigLogged)
            {
                _invalidConfigLogged = true;
                Debug.WriteLine(
                    $"[FFT '{Name}'] Invalid input ({fftSize} samples × {channels} ch): FFT size must be a " +
                    "power of 2. Set the upstream SlidingWindow buffer size to a power of 2.");
            }
            return false;
        }

        _invalidConfigLogged = false;
        _fftSize = fftSize;
        _channels = channels;
        _outputBins = fftSize / 2 + 1;

        _fftEngines = new FastFourierTransform[channels];
        for (int ch = 0; ch < channels; ch++)
            _fftEngines[ch] = new FastFourierTransform(fftSize);

        _fftBuffers = new Complex[channels][];
        for (int ch = 0; ch < channels; ch++)
            _fftBuffers[ch] = new Complex[fftSize];

        _spectrumScratch = new double[_outputBins];
        _windowCoeffs = ComputeWindow(_windowType, fftSize);

        UpdateFrequencyAxis();

        CurrentFftSize = fftSize;
        OutputBinCount = _outputBins;
        ChannelCount = channels;
        OnPropertyChanged(nameof(FftDescription));

        _initialized = true;
        Debug.WriteLine($"[FFT '{Name}'] Ready: {fftSize}pt, {channels}ch, {_outputBins} bins, {_windowType}, {_outputMode}");
        return true;
    }

    /// <summary>Recalculates the frequency axis when the block (tick) rate changes.</summary>
    protected override void OnDesiredRateChanged(double oldRate, double newRate)
    {
        base.OnDesiredRateChanged(oldRate, newRate);
        UpdateFrequencyAxis();
    }

    /// <summary>Recalculates the frequency axis when the true signal sample rate changes.</summary>
    protected override void OnSignalRateChanged(double newRate)
    {
        base.OnSignalRateChanged(newRate);
        UpdateFrequencyAxis();
    }

    /// <summary>
    /// Rebuilds <see cref="FrequencyAxis"/> and updates spectrogram frequency bounds.
    /// Priority: the live <see cref="BaseBlock.SignalRate"/> (the true sample rate, which survives
    /// windowing and tracks runtime pipeline-rate changes) → the explicit <see cref="_sampleRate"/>
    /// override (Params[5], used only when the pipeline supplies no SignalRate) →
    /// <see cref="BaseBlock.DesiredRate"/>. <see cref="BaseBlock.InputRate"/> is deliberately not
    /// used: for a windowed input it is the window fire rate (sampleRate / stride), which would
    /// scale the axis down by the stride.
    /// </summary>
    private void UpdateFrequencyAxis()
    {
        if (_fftSize <= 0) return;

        double rate = SignalRate > 0   ? SignalRate
                    : _sampleRate > 0  ? _sampleRate
                    : DesiredRate > 0  ? DesiredRate
                    : 0;

        if (rate <= 0) return;

        FrequencyResolution = rate / _fftSize;
        FrequencyAxis = new double[_outputBins];
        for (int i = 0; i < _outputBins; i++)
            FrequencyAxis[i] = i * FrequencyResolution;

        Spectrogram.MinFrequency = 0;
        Spectrogram.MaxFrequency = (_outputBins - 1) * FrequencyResolution;

        OnPropertyChanged(nameof(FftDescription));

        string rateSource = SignalRate > 0 ? "signal" : _sampleRate > 0 ? "explicit" : "desired";
        Debug.WriteLine($"[FFT '{Name}'] FreqAxis: rate={rate:F1} Hz, Δf={FrequencyResolution:F2} Hz, " +
                        $"maxF={Spectrogram.MaxFrequency:F1} Hz (source: {rateSource})");
    }

    /// <summary>
    /// Processes incoming data: accepts matrices (from SlidingWindow) or vectors (single-channel).
    /// </summary>
    /// <param name="sender">The upstream block.</param>
    /// <param name="data">A <see cref="Matrix{T}"/> or <see cref="Vector{T}"/>.</param>
    protected override void OnReceive(object sender, object data)
    {
        if (data is null) return;

        switch (data)
        {
            case Matrix<double> window:
                ProcessWindow(window);
                break;
            case MathNet.Numerics.LinearAlgebra.Vector<double> v:
                ProcessWindow(DenseMatrix.Create(v.Count, 1, (r, _) => v[r]));
                break;
        }
    }

    /// <summary>
    /// Computes the FFT for each channel, updates the spectrogram, and publishes the result.
    /// </summary>
    /// <param name="window">Input matrix (rows = samples, columns = channels).</param>
    private void ProcessWindow(Matrix<double> window)
    {
        int rows = window.RowCount;
        int cols = window.ColumnCount;

        if (!EnsureInit(rows, cols))
            return;

        // Snapshot the user-tunable settings once so the whole frame is computed with a single,
        // consistent configuration even if the UI thread changes window/mode/dB mid-frame.
        var mode = _outputMode;
        var coeffs = _windowCoeffs!;
        double dbRef = Volatile.Read(ref _dbRef);
        double dbFloor = Volatile.Read(ref _dbFloor);

        var output = DenseMatrix.Create(_outputBins, cols, 0.0);

        for (int ch = 0; ch < cols; ch++)
        {
            var buffer = _fftBuffers![ch];
            for (int i = 0; i < rows; i++)
                buffer[i] = new Complex(window[i, ch] * coeffs[i], 0.0);
            _fftEngines![ch].Forward(buffer);
            ExtractSpectrum(buffer, output, ch, mode, dbRef, dbFloor);
        }

        PushToSpectrogram(output, cols, mode, dbRef, dbFloor);

        _frameCount++;
        if (_frameCount <= 3 || _frameCount % 500 == 0)
        {
            double maxVal = 0, minDb = double.MaxValue, maxDb = double.MinValue;
            for (int k = 0; k < _outputBins; k++)
            {
                if (output[k, 0] > maxVal) maxVal = output[k, 0];
                if (_spectrumScratch![k] < minDb) minDb = _spectrumScratch[k];
                if (_spectrumScratch[k] > maxDb) maxDb = _spectrumScratch[k];
            }
            Debug.WriteLine($"[FFT '{Name}'] #{_frameCount}: {_outputBins} bins, {cols}ch, " +
                            $"maxOutput={maxVal:G4}, spectrogramDb=[{minDb:F1}..{maxDb:F1}]");
        }

        Publish(output);
    }

    /// <summary>
    /// Extracts the single-sided spectrum from one channel's FFT result into the output matrix,
    /// scaled per <paramref name="mode"/>. The DC and Nyquist bins are unpaired and so are not
    /// doubled — this single-sided correction applies only to <see cref="OutputMode.MagnitudeNormalized"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExtractSpectrum(Complex[] fftResult, Matrix<double> output, int channel,
                                 OutputMode mode, double dbRef, double dbFloor)
    {
        double scale = 2.0 / _fftSize;
        int last = _outputBins - 1;
        for (int k = 0; k < _outputBins; k++)
        {
            double mag = fftResult[k].Magnitude;
            bool edge = k == 0 || k == last;
            output[k, channel] = mode switch
            {
                OutputMode.Magnitude => mag,
                OutputMode.MagnitudeNormalized => edge ? mag / _fftSize : mag * scale,
                OutputMode.Power => mag * mag,
                OutputMode.DB => MagnitudeToDb(mag, dbRef, dbFloor),
                _ => mag
            };
        }
    }

    /// <summary>
    /// Pushes the current spectrum frame to the spectrogram (always in dB for display).
    /// </summary>
    private void PushToSpectrogram(Matrix<double> output, int cols, OutputMode mode, double dbRef, double dbFloor)
    {
        var scratch = _spectrumScratch!;
        int sel = SelectedChannel;
        int last = _outputBins - 1;

        if (sel >= 0 && sel < cols)
        {
            for (int k = 0; k < _outputBins; k++)
                scratch[k] = ToDbForDisplay(output[k, sel], mode, k == 0 || k == last, dbRef, dbFloor);
        }
        else
        {
            double inv = 1.0 / cols;
            for (int k = 0; k < _outputBins; k++)
            {
                double sum = 0;
                for (int ch = 0; ch < cols; ch++)
                    sum += output[k, ch];
                scratch[k] = ToDbForDisplay(sum * inv, mode, k == 0 || k == last, dbRef, dbFloor);
            }
        }

        Spectrogram.EnqueueSpectrum(scratch);
    }

    /// <summary>
    /// Converts a mode-scaled output value back to dB for spectrogram display.
    /// <paramref name="edge"/> marks the DC/Nyquist bins, whose normalized scaling differs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double ToDbForDisplay(double value, OutputMode mode, bool edge, double dbRef, double dbFloor)
    {
        if (mode == OutputMode.DB) return Math.Max(value, dbFloor);

        double mag = mode switch
        {
            OutputMode.Power => Math.Sqrt(Math.Max(value, 0)),
            OutputMode.MagnitudeNormalized => value * (edge ? _fftSize : _fftSize / 2.0),
            _ => value
        };

        if (mag <= 0) return dbFloor;
        return Math.Max(20.0 * Math.Log10(mag / dbRef), dbFloor);
    }

    /// <summary>Converts magnitude to dB with floor clamping.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double MagnitudeToDb(double magnitude, double dbRef, double dbFloor)
    {
        if (magnitude <= 0) return dbFloor;
        return Math.Max(20.0 * Math.Log10(magnitude / dbRef), dbFloor);
    }

    /// <summary>
    /// Computes window function coefficients. A size ≤ 1 window degenerates to pass-through
    /// (all 1.0), which also avoids the divide-by-(size−1) that would otherwise produce NaN.
    /// </summary>
    /// <param name="type">Window type.</param>
    /// <param name="size">Window size in samples.</param>
    /// <returns>Array of window coefficients.</returns>
    private static double[] ComputeWindow(WindowType type, int size)
    {
        var w = new double[size];

        if (type == WindowType.Rectangular || size <= 1)
        {
            Array.Fill(w, 1.0);
            return w;
        }

        double N = size - 1;
        switch (type)
        {
            case WindowType.Hann:
                for (int i = 0; i < size; i++)
                    w[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / N));
                break;
            case WindowType.Hamming:
                for (int i = 0; i < size; i++)
                    w[i] = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / N);
                break;
            case WindowType.Blackman:
                for (int i = 0; i < size; i++)
                    w[i] = 0.42 - 0.5 * Math.Cos(2.0 * Math.PI * i / N)
                                 + 0.08 * Math.Cos(4.0 * Math.PI * i / N);
                break;
        }
        return w;
    }

    /// <summary>Disposes the spectrogram and base class resources (including CSV dumper).</summary>
    public override void Dispose()
    {
        Spectrogram.Dispose();
        base.Dispose();
    }
}
