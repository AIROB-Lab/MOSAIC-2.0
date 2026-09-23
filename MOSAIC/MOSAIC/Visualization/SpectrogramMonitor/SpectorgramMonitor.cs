using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Services;
using MOSAIC.Visualization.Heatmap;
using SkiaSharp;

namespace MOSAIC.Visualization.SpectrogramMonitor;

/// <summary>
/// A reusable spectrogram visualization monitor: buffers incoming spectral frames in a
/// ring buffer and renders them to a scrolling bitmap with a colorbar legend. Lifecycle is
/// <c>Pause()</c> / <c>Resume()</c> / <c>ResetData()</c> / <c>Dispose()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>From a processing block:</b>
/// <code>
/// _spectrogram.EnqueueSpectrum(magnitudeArray);
/// </code>
/// </para>
/// <para>
/// <b>From XAML (via SpectrogramMonitorView):</b>
/// <code>
/// &lt;local:SpectrogramMonitorView Spectrogram="{Binding MySpectrogram}"/&gt;
/// </code>
/// </para>
/// <para>
/// Colormaps use an <see cref="SKColor"/>[] palette. Call <see cref="SetColormap"/> directly,
/// or use the extension methods in <see cref="SpectrogramColormaps"/> (UseViridis, UseInferno, …).
/// </para>
/// </remarks>
public partial class SpectrogramMonitor : ObservableObject, IDisposable, IMonitor
{
    #region Fields

    private readonly Action _timerCallback;

    private readonly ConcurrentQueue<double[]> _queue = new();

    private static readonly ArrayPool<double> _pool = ArrayPool<double>.Shared;

    private volatile bool _disposed;

    private volatile bool _isActive;

    private volatile bool _isUpdating;

    private int _flushScheduled;

    private readonly int _maxDepth;

    private double[][]? _buffer;

    private int _writeIndex;

    private int _count;

    private int _bins;

    private double _peakMax = double.NaN;

    private double _peakMin = double.NaN;

    private DateTime _lastScaleUpdate = DateTime.UtcNow;

    private SKColor[] _colormap;

    private string? _colormapName;

    private SKBitmap? _skBitmap;

    private int _bitmapWidth;

    private int _bitmapHeight;

    // Pixel buffer reused across frames to avoid allocation
    private byte[]? _pixelBuffer;

    private VisualizationTimer.TickRate _tickRate = VisualizationTimer.TickRate.Fps30;

    private int _reconfigureScheduled;

    private int _pendingBinCount;

    #endregion

    #region Observable properties

    /// <summary>The rendered spectrogram bitmap. Bind to <c>Image.Source</c>.</summary>
    [ObservableProperty] 
    private Bitmap? _image;

    /// <summary>When true, color scale min/max are computed from data with hysteresis.</summary>
    [ObservableProperty] 
    private bool _autoScale = true;

    /// <summary>Minimum value for color mapping (dark end).</summary>
    [ObservableProperty] 
    private double _colorMin;

    /// <summary>Maximum value for color mapping (bright end).</summary>
    [ObservableProperty] 
    private double _colorMax = 1.0;

    /// <summary>Number of time slices currently buffered.</summary>
    [ObservableProperty] 
    private int _frameCount;

    /// <summary>Number of frequency bins per frame.</summary>
    [ObservableProperty] 
    private int _binCount;

    /// <summary>Whether any data has been received.</summary>
    [ObservableProperty] 
    private bool _hasData;

    /// <summary>Minimum frequency (Hz) — bottom of spectrogram (DC). Set by the FFT block.</summary>
    [ObservableProperty] 
    private double _minFrequency;

    /// <summary>Maximum frequency (Hz) — top of spectrogram (Nyquist). Set by the FFT block.</summary>
    [ObservableProperty] 
    private double _maxFrequency;

    // Colorbar
    [ObservableProperty] 
    private Bitmap? _colorbarBitmap;

    [ObservableProperty] 
    private string _colorbarMinLabel = "min";

    [ObservableProperty] 
    private string _colorbarMaxLabel = "max";

    [ObservableProperty] 
    private string _colorbarCaption = "";

    #endregion

    #region Public properties

    /// <summary>Peak-hold decay half-life in seconds. Longer = more stable scale. Default 3s.</summary>
    public double DecayHalfLife { get; set; } = 3.0;

    /// <summary>How fast the scale expands to capture new peaks (0..1). 1.0 = instant.</summary>
    public double ExpandAlpha { get; set; } = 0.8;

    /// <summary>Maximum time slices in ring buffer (spectrogram width).</summary>
    public int MaxDepth => _maxDepth;

    /// <summary>
    /// Gets the current colormap name (for display in settings flyout).
    /// </summary>
    public string? ColormapName => _colormapName;

    public VisualizationTimer.TickRate TickRate
    {
        get => _tickRate;
        set
        {
            if (_tickRate == value) return;
            var wasActive = _isActive;
            if (wasActive) Pause();
            _tickRate = value;
            if (wasActive) Resume();
        }
    }

    #endregion

    #region Construction

    /// <param name="maxDepth">Maximum number of time slices (spectrogram width).</param>
    public SpectrogramMonitor(int maxDepth = 200)
    {
        _maxDepth = Math.Max(20, maxDepth);
        _timerCallback = RequestFlush;

        // Default: Inferno 256
        _colormap = BuildInferno256();
        _colormapName = "Inferno";
        RebuildColorbar();
    }

    #endregion

    #region Public API

    /// <summary>
    /// Enqueues a spectral frame from the processing thread.
    /// The array is copied via ArrayPool — caller can reuse their buffer immediately.
    /// Data is always accepted regardless of whether rendering is active,
    /// so frames are buffered and ready when the view attaches.
    /// </summary>
    public void EnqueueSpectrum(double[] spectrum)
    {
        if (_disposed || spectrum is null || spectrum.Length == 0) return;
        if (_isUpdating) return;

        // Cap queue size to prevent unbounded growth when no view is rendering
        if (_queue.Count > _maxDepth * 2)
        {
            // Drop oldest frames
            while (_queue.Count > _maxDepth && _queue.TryDequeue(out var old))
                _pool.Return(old);
        }

        int len = spectrum.Length;

        if (_bins > 0 && len != _bins)
        {
            RequestBinReconfigure(len);
            return;
        }

        var buf = _pool.Rent(len);
        Array.Copy(spectrum, buf, len);
        _queue.Enqueue(buf);
    }

    /// <summary>
    /// Enqueues a spectrum from a ReadOnlySpan (zero-alloc path).
    /// </summary>
    public void EnqueueSpectrum(ReadOnlySpan<double> spectrum)
    {
        if (_disposed || spectrum.Length == 0) return;
        if (_isUpdating) return;

        int len = spectrum.Length;

        if (_bins > 0 && len != _bins)
        {
            RequestBinReconfigure(len);
            return;
        }

        var buf = _pool.Rent(len);
        spectrum.CopyTo(buf.AsSpan(0, len));
        _queue.Enqueue(buf);
    }

    public void Pause()
    {
        _isActive = false;
        VisualizationTimer.Instance.Unsubscribe(_timerCallback);
        DrainQueue();
    }

    public void Resume()
    {
        if (_disposed) return;
        _isActive = true;
        VisualizationTimer.Instance.Subscribe(_timerCallback, _tickRate);
    }

    public void ResetData()
    {
        if (_disposed) return;

        if (Dispatcher.UIThread.CheckAccess())
            ResetDataCore();
        else
            Dispatcher.UIThread.Post(ResetDataCore, DispatcherPriority.Render);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isActive = false;
        VisualizationTimer.Instance.Unsubscribe(_timerCallback);
        DrainQueue();
        _buffer = null;
        _skBitmap?.Dispose();
        _skBitmap = null;
        Image = null;
    }

    /// <summary>
    /// Sets the colormap. Accepts an array of <see cref="SKColor"/> (same format as HeatMap).
    /// </summary>
    public void SetColormap(SKColor[] colors, string? name = null)
    {
        if (colors is null || colors.Length < 2) return;
        _colormap = colors;
        _colormapName = name;
        RebuildColorbar();
    }

    /// <summary>
    /// Sets a fixed color scale range (disables auto-scaling).
    /// </summary>
    public void UseFixedScale(double min, double max)
    {
        if (min >= max) return;
        AutoScale = false;
        ColorMin = min;
        ColorMax = max;
        _peakMax = max;
        _peakMin = min;
        UpdateLegendLabels();
    }

    /// <summary>
    /// Enables auto-scaling (resets tracked range).
    /// </summary>
    public void UseAutoScale()
    {
        AutoScale = true;
        _peakMax = double.NaN;
        _peakMin = double.NaN;
        _lastScaleUpdate = DateTime.UtcNow;
    }

    public void RebuildColorbar(int width = 18, int height = 180)
    {
        if (_colormap is null || _colormap.Length < 2) return;

        try
        {
            // Sample to at most 64 stops for the gradient
            SKColor[] colors;
            float[] stops;
            const int maxStops = 64;

            if (_colormap.Length <= maxStops)
            {
                colors = _colormap;
                stops = Enumerable.Range(0, colors.Length)
                    .Select(i => i / (float)(colors.Length - 1))
                    .ToArray();
            }
            else
            {
                colors = new SKColor[maxStops];
                stops = new float[maxStops];
                for (int i = 0; i < maxStops; i++)
                {
                    float t = i / (float)(maxStops - 1);
                    int srcIdx = Math.Min((int)(t * (_colormap.Length - 1)), _colormap.Length - 1);
                    colors[i] = _colormap[srcIdx];
                    stops[i] = t;
                }
            }

            using var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bmp);
            using var paint = new SKPaint { IsAntialias = true };

            // High values at top
            var startPt = new SKPoint(0, height);
            var endPt = new SKPoint(0, 0);
            paint.Shader = SKShader.CreateLinearGradient(startPt, endPt, colors, stops, SKShaderTileMode.Clamp);
            canvas.Clear(new SKColor(0, 0, 0, 0));
            canvas.DrawRect(new SKRect(0, 0, width, height), paint);

            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = data.AsStream();
            ColorbarBitmap = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SpectrogramMonitor] RebuildColorbar error: {ex.Message}");
        }
    }

    public void UpdateThemeColors()
    {
        // Spectrogram is bitmap-based, so the only theme-sensitive things
        // are the legend labels — they're just strings, the View handles colors.
        // But we trigger a notify so bindings refresh.
        if (_disposed) return;
        OnPropertyChanged(nameof(ColorbarMinLabel));
        OnPropertyChanged(nameof(ColorbarMaxLabel));
        OnPropertyChanged(nameof(ColorbarCaption));
    }

    #endregion

    #region Rendering pipeline

    private void ResetDataCore()
    {
        if (_disposed) return;

        _isUpdating = true;
        try
        {
            DrainQueue();
            _buffer = null;
            _writeIndex = 0;
            _count = 0;
            _bins = 0;
            _skBitmap?.Dispose();
            _skBitmap = null;
            _bitmapWidth = 0;
            _bitmapHeight = 0;
            _peakMax = double.NaN;
            _peakMin = double.NaN;
            _lastScaleUpdate = DateTime.UtcNow;

            Image = null;
            HasData = false;
            FrameCount = 0;
            BinCount = 0;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void RequestBinReconfigure(int newBinCount)
    {
        if (_disposed || newBinCount <= 0) return;

        Volatile.Write(ref _pendingBinCount, newBinCount);
        if (Interlocked.Exchange(ref _reconfigureScheduled, 1) == 1) return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _reconfigureScheduled, 0);
            if (_disposed) return;

            var binCount = Volatile.Read(ref _pendingBinCount);

            _isUpdating = true;
            try
            {
                DrainQueue();
                InitBuffer(binCount);
            }
            finally
            {
                _isUpdating = false;
            }
        }, DispatcherPriority.Render);
    }

    private void InitBuffer(int bins)
    {
        _bins = bins;
        _buffer = new double[_maxDepth][];
        for (int i = 0; i < _maxDepth; i++)
            _buffer[i] = new double[_bins];
        _writeIndex = 0;
        _count = 0;
        _peakMax = double.NaN;
        _peakMin = double.NaN;
        _lastScaleUpdate = DateTime.UtcNow;
        BinCount = bins;
    }

    private void RequestFlush()
    {
        if (_disposed || !_isActive || _isUpdating) return;
        if (_queue.IsEmpty) return;

        if (Interlocked.Exchange(ref _flushScheduled, 1) == 1) return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            FlushAndRender();
        }, DispatcherPriority.Render);
    }

    private void FlushAndRender()
    {
        if (_disposed || !_isActive || _isUpdating) return;

        _isUpdating = true;
        try
        {
            // Drain queue into ring buffer, return pooled arrays
            while (_queue.TryDequeue(out var frame))
            {
                PushToBuffer(frame);
                _pool.Return(frame);
            }

            if (_buffer == null || _count == 0) return;

            // Auto-scale with hysteresis
            if (AutoScale)
                UpdateAutoScale();

            UpdateLegendLabels();

            // Render to bitmap
            RenderBitmap();

            FrameCount = _count;
            HasData = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SpectrogramMonitor] FlushAndRender error: {ex.Message}");
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void PushToBuffer(double[] frame)
    {
        // Init on first frame
        if (_buffer == null || _bins == 0)
        {
            InitBuffer(frame.Length);
        }

        // Pooled array is always >= _bins; skip if somehow shorter
        if (frame.Length < _bins)
            return;

        Array.Copy(frame, _buffer![_writeIndex], _bins);
        _writeIndex = (_writeIndex + 1) % _maxDepth;
        if (_count < _maxDepth) _count++;
    }

    private void UpdateAutoScale()
    {
        if (_buffer == null || _count == 0) return;

        // Find current min/max across the visible ring buffer
        double frameMin = double.PositiveInfinity;
        double frameMax = double.NegativeInfinity;
        int start = (_writeIndex - _count + _maxDepth) % _maxDepth;

        for (int t = 0; t < _count; t++)
        {
            int idx = (start + t) % _maxDepth;
            var slice = _buffer[idx];
            for (int f = 0; f < _bins; f++)
            {
                var v = slice[f];
                if (!double.IsFinite(v)) continue;
                if (v < frameMin) frameMin = v;
                if (v > frameMax) frameMax = v;
            }
        }

        if (!double.IsFinite(frameMin) || !double.IsFinite(frameMax)) return;

        var now = DateTime.UtcNow;
        double dt = Math.Max(1e-3, (now - _lastScaleUpdate).TotalSeconds);
        _lastScaleUpdate = now;
        double decay = Math.Exp(-Math.Log(2) * dt / Math.Max(0.1, DecayHalfLife));

        if (double.IsNaN(_peakMax) || double.IsNaN(_peakMin))
        {
            _peakMax = frameMax;
            _peakMin = frameMin;
        }
        else
        {
            // Max: expand up fast, decay down slow
            if (frameMax > _peakMax)
                _peakMax += ExpandAlpha * (frameMax - _peakMax);
            else
                _peakMax = Math.Max(frameMax, _peakMax * decay + frameMax * (1 - decay));

            // Min: expand down fast, contract up slow
            if (frameMin < _peakMin)
                _peakMin += ExpandAlpha * (frameMin - _peakMin);
            else
                _peakMin = _peakMin * decay + frameMin * (1 - decay);
        }

        // Ensure minimum span
        double span = _peakMax - _peakMin;
        if (span < 1.0)
        {
            double mid = 0.5 * (_peakMin + _peakMax);
            _peakMin = mid - 0.5;
            _peakMax = mid + 0.5;
        }

        ColorMin = _peakMin;
        ColorMax = _peakMax;
    }

    private void UpdateLegendLabels()
    {
        ColorbarMinLabel = FormatValue(ColorMin);
        ColorbarMaxLabel = FormatValue(ColorMax);
        ColorbarCaption = AutoScale ? "auto" : "fixed";
    }

    private void RenderBitmap()
    {
        if (_buffer == null || _count == 0 || _bins == 0) return;

        int width = _maxDepth;  // Always fixed width
        int height = _bins;

        // Create bitmap once at fixed size
        if (_skBitmap == null || _bitmapWidth != width || _bitmapHeight != height)
        {
            _skBitmap?.Dispose();
            _skBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            _bitmapWidth = width;
            _bitmapHeight = height;
            _pixelBuffer = new byte[width * height * 4];
        }

        double range = ColorMax - ColorMin;
        if (range < 1e-12) range = 1.0;
        double invRange = 1.0 / range;
        int cmapMax = _colormap.Length - 1;
        
        int dataStart = (_writeIndex - _count + _maxDepth) % _maxDepth;

        // How many empty columns on the left (before data has filled the buffer)
        int emptyColumns = width - _count;

        var buf = _pixelBuffer!;
        var black = _colormap[0]; // Empty columns use the lowest color

        for (int y = 0; y < height; y++)
        {
            int freqIdx = height - 1 - y;
            int rowOffset = y * width * 4;

            // Empty columns (left side, before data arrived)
            for (int x = 0; x < emptyColumns; x++)
            {
                int px = rowOffset + x * 4;
                buf[px + 0] = black.Blue;
                buf[px + 1] = black.Green;
                buf[px + 2] = black.Red;
                buf[px + 3] = 255;
            }

            // Data columns
            for (int x = 0; x < _count; x++)
            {
                int bufIdx = (dataStart + x) % _maxDepth;
                double normalized = (_buffer[bufIdx][freqIdx] - ColorMin) * invRange;
                normalized = Math.Clamp(normalized, 0.0, 1.0);

                int ci = Math.Min((int)(normalized * cmapMax), cmapMax);
                var c = _colormap[ci];

                int px = rowOffset + (emptyColumns + x) * 4;
                buf[px + 0] = c.Blue;
                buf[px + 1] = c.Green;
                buf[px + 2] = c.Red;
                buf[px + 3] = 255;
            }
        }

        // Copy pixel buffer into SKBitmap
        var pinnedHandle = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            _skBitmap.InstallPixels(info, pinnedHandle.AddrOfPinnedObject(), width * 4);

            using var img = SKImage.FromBitmap(_skBitmap);
            using var data = img.Encode(SKEncodedImageFormat.Png, 80);
            using var stream = data.AsStream();
            Image = new Bitmap(stream);
        }
        finally
        {
            pinnedHandle.Free();
        }
    }

    #endregion

    #region Helpers

    private void DrainQueue()
    {
        while (_queue.TryDequeue(out var buf))
            _pool.Return(buf);
    }

    private static string FormatValue(double v)
    {
        double abs = Math.Abs(v);
        if (abs == 0) return "0";
        if (abs >= 1000) return v.ToString("F0");
        if (abs >= 1) return v.ToString("F2");
        if (abs >= 0.01) return v.ToString("F3");
        return v.ToString("G3");
    }

    private static SKColor[] BuildInferno256()
    {
        string[] anchors =
        [
            "#000004", "#160B39", "#420A68", "#6A176E", "#932667",
            "#BC3754", "#DD513A", "#F37819", "#FCA50A", "#F0F921"
        ];
        var parsed = anchors.Select(SKColor.Parse).ToArray();
        var result = new SKColor[256];
        for (int i = 0; i < 256; i++)
        {
            float t = i / 255f;
            float p = t * (parsed.Length - 1);
            int a = (int)Math.Floor(p);
            int b = Math.Min(a + 1, parsed.Length - 1);
            float u = p - a;
            result[i] = LerpColor(parsed[a], parsed[b], u);
        }
        return result;
    }

    private static SKColor LerpColor(in SKColor c1, in SKColor c2, float t)
    {
        byte r = (byte)(c1.Red   + (c2.Red   - c1.Red)   * t);
        byte g = (byte)(c1.Green + (c2.Green - c1.Green) * t);
        byte b = (byte)(c1.Blue  + (c2.Blue  - c1.Blue)  * t);
        return new SKColor(r, g, b);
    }

    #endregion
}