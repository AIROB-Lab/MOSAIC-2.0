using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MOSAIC.Services;
using SkiaSharp;

namespace MOSAIC.Visualization.Scatter3D;

/// <summary>
/// A lightweight 3D scatter plot rendered via SkiaSharp orthographic projection.
/// No GPU, no scene graph — just a rotation matrix + 2D drawing.
/// </summary>
/// <remarks>
/// <para><b>From any thread:</b> call <see cref="AddPoint"/>. <b>From XAML:</b> bind to
/// <see cref="Image"/> via <see cref="Scatter3DView"/>. Camera is driven by <see cref="Azimuth"/>
/// and <see cref="Elevation"/> (degrees); the view maps mouse drag to those properties.</para>
/// <para><b>Rendering:</b> draws straight into a double-buffered <see cref="WriteableBitmap"/>
/// (no PNG encode/decode), reusing all per-frame buffers and paints, so the render path allocates
/// nothing in steady state — stable frame timing at any add rate and large point counts.</para>
/// </remarks>
public partial class Scatter3DMonitor : ObservableObject, IDisposable
{
    #region Constants

    private const int BitmapWidth = 800;
    private const int BitmapHeight = 800;
    private const float Padding = 60f;

    private static readonly SKColor BackgroundColor = new(0x1A, 0x1A, 0x2E);

    #endregion

    #region Fields

    // Live stream — ring buffer (trimmed by MaxPoints)
    private Point3D[] _ringBuffer;
    private int[] _ringClasses;
    private int _ringWrite;
    private int _ringCount;
    private int _maxPoints;

    // Frozen clusters — never trimmed, always drawn
    private Point3D[] _frozenPoints = Array.Empty<Point3D>();
    private int[] _frozenClasses = Array.Empty<int>();
    private int _frozenCount;

    private readonly object _dataLock = new();

    // Axis bounds (expand-only for a stable view)
    private double _minX, _maxX, _minY, _maxY, _minZ, _maxZ;
    private bool _boundsInitialized;

    // Reused per-frame render buffers (grow-only): merged points → projection → depth sort
    private Point3D[] _pts = Array.Empty<Point3D>();
    private int[] _cls = Array.Empty<int>();
    private float[] _projSx = Array.Empty<float>();
    private float[] _projSy = Array.Empty<float>();
    private float[] _depthKey = Array.Empty<float>();
    private int[] _order = Array.Empty<int>();

    // Reused per-frame scratch
    private readonly Dictionary<int, (double sx, double sy, double sz, int count)> _centroidSums = new();
    private readonly Dictionary<int, int> _legendCounts = new();
    private readonly List<int> _legendKeys = new();

    // Double-buffered output (ref-swapped each frame to drive the binding)
    private WriteableBitmap? _wbA, _wbB;
    private bool _useA = true;

    // Render scheduling
    private readonly Action _timerCallback;
    private volatile bool _disposed;
    private volatile bool _isActive;
    private volatile bool _needsRender;
    private int _renderScheduled;

    #endregion

    #region Paints (created once, mutated per draw, never reallocated)

    private readonly SKPaint _pointPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _edgePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f };
    private readonly SKPaint _centroidPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _centroidEdgePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
    private readonly SKPaint _infoPaint = new() { IsAntialias = true, TextSize = 14, Color = new SKColor(0xAA, 0xAA, 0xAA, 0x99), Typeface = SKTypeface.FromFamilyName("Arial") };
    private readonly SKPaint _gridPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, Color = new SKColor(0xFF, 0xFF, 0xFF, 0x0E) };
    private readonly SKPaint _axisPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
    private readonly SKPaint _negPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, PathEffect = SKPathEffect.CreateDash(new float[] { 5, 4 }, 0) };
    private readonly SKPaint _arrowPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _labelPaint = new() { IsAntialias = true, TextSize = 22, Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) };
    private readonly SKPaint _labelBgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x1A, 0x1A, 0x2E, 0xDD) };
    private readonly SKPaint _originPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0xFF, 0xFF, 0xFF, 0x70) };
    private readonly SKPaint _legendTextPaint = new() { IsAntialias = true, TextSize = 13, Color = new SKColor(0xCC, 0xCC, 0xCC), Typeface = SKTypeface.FromFamilyName("Arial") };
    private readonly SKPaint _legendDotPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _legendBgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x1A, 0x1A, 0x2E, 0xCC) };
    private readonly SKPath _scratchPath = new();

    #endregion

    #region Observable Properties

    [ObservableProperty] private Bitmap? _image;

    /// <summary>Camera azimuth angle in degrees (horizontal rotation).</summary>
    [ObservableProperty] private double _azimuth = -35;

    /// <summary>Camera elevation angle in degrees (vertical tilt).</summary>
    [ObservableProperty] private double _elevation = 25;

    /// <summary>Camera zoom level. 1.0 = default, 2.0 = 2× closer.</summary>
    [ObservableProperty] private double _zoom = 1.0;

    /// <summary>Axis labels. Default: X, Y, Z.</summary>
    [ObservableProperty] private string _xLabel = "X";
    [ObservableProperty] private string _yLabel = "Y";
    [ObservableProperty] private string _zLabel = "Z";

    [ObservableProperty] private int _pointCount;
    [ObservableProperty] private bool _hasData;

    public VisualizationTimer.TickRate TickRate { get; set; } = VisualizationTimer.TickRate.Fps30;

    partial void OnAzimuthChanged(double value) => _needsRender = true;
    partial void OnElevationChanged(double value) => _needsRender = true;
    partial void OnZoomChanged(double value) => _needsRender = true;

    #endregion

    #region Class Colors

    /// <summary>Fallback palette, used when no override is set for a class index.</summary>
    private static readonly SKColor[] DefaultPalette =
    {
        new(0xFF, 0x14, 0x93), // deep pink (live default)
        new(0xFF, 0x7F, 0x0E), // orange
        new(0x2C, 0xA0, 0x2C), // green
        new(0xD6, 0x27, 0x28), // red
        new(0x94, 0x67, 0xBD), // purple
        new(0x8C, 0x56, 0x4B), // brown
        new(0xE3, 0x77, 0xC2), // pink
        new(0xBC, 0xBD, 0x22), // olive
        new(0x17, 0xBE, 0xCF), // cyan
        new(0x7F, 0x7F, 0x7F), // gray
    };

    private readonly Dictionary<int, SKColor> _classColorOverrides = new();

    /// <summary>Set a custom color for a class index (sync with the UI legend).</summary>
    public void SetClassColor(int classIndex, SKColor color)
    {
        _classColorOverrides[classIndex] = color;
        _needsRender = true;
    }

    /// <summary>Clear all custom color overrides.</summary>
    public void ClearClassColors()
    {
        _classColorOverrides.Clear();
        _needsRender = true;
    }

    private SKColor GetClassColor(int classIndex)
        => _classColorOverrides.TryGetValue(classIndex, out var c)
            ? c
            : DefaultPalette[Math.Abs(classIndex) % DefaultPalette.Length];

    #endregion

    #region Lifecycle

    public Scatter3DMonitor(int maxPoints = 2000)
    {
        _maxPoints = Math.Max(100, maxPoints);
        _ringBuffer = new Point3D[_maxPoints];
        _ringClasses = new int[_maxPoints];
        _timerCallback = OnTimerTick;
    }

    public void Resume()
    {
        if (_disposed) return;
        _isActive = true;
        VisualizationTimer.Instance.Subscribe(_timerCallback, TickRate);
        _needsRender = true;
    }

    public void Pause()
    {
        _isActive = false;
        VisualizationTimer.Instance.Unsubscribe(_timerCallback);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Pause();

        _pointPaint.Dispose(); _edgePaint.Dispose();
        _centroidPaint.Dispose(); _centroidEdgePaint.Dispose(); _infoPaint.Dispose();
        _gridPaint.Dispose(); _axisPaint.Dispose(); _negPaint.Dispose(); _arrowPaint.Dispose();
        _labelPaint.Dispose(); _labelBgPaint.Dispose(); _originPaint.Dispose();
        _legendTextPaint.Dispose(); _legendDotPaint.Dispose(); _legendBgPaint.Dispose();
        _scratchPath.Dispose();
        _wbA?.Dispose(); _wbB?.Dispose();
    }

    #endregion

    #region Data Input (any thread)

    /// <summary>Add a single streaming point. Thread-safe; overwrites the oldest when full.</summary>
    public void AddPoint(double x, double y, double z, int classLabel = 0)
    {
        if (_disposed) return;
        lock (_dataLock)
        {
            _ringBuffer[_ringWrite] = new Point3D(x, y, z);
            _ringClasses[_ringWrite] = classLabel;
            _ringWrite = (_ringWrite + 1) % _maxPoints;
            if (_ringCount < _maxPoints) _ringCount++;
        }
        _needsRender = true;
        HasData = true;
    }

    /// <summary>Add a single streaming point from a vector (uses the first 3 components).</summary>
    public void AddPoint(MathNet.Numerics.LinearAlgebra.Vector<double> v, int classLabel = 0)
    {
        if (v is null || v.Count < 3) return;
        AddPoint(v[0], v[1], v[2], classLabel);
    }

    /// <summary>Set frozen cluster points — never trimmed by the slider; always drawn behind the live stream.</summary>
    public void SetFrozenPoints(Point3D[] points, int[]? classes = null)
    {
        if (_disposed) return;
        lock (_dataLock)
        {
            if (points is null || points.Length == 0)
            {
                _frozenPoints = Array.Empty<Point3D>();
                _frozenClasses = Array.Empty<int>();
                _frozenCount = 0;
            }
            else
            {
                _frozenPoints = (Point3D[])points.Clone();
                _frozenClasses = classes != null ? (int[])classes.Clone() : new int[points.Length];
                _frozenCount = points.Length;
            }
        }
        HasData = _frozenCount > 0 || _ringCount > 0;
        _needsRender = true;
    }

    /// <summary>Clear frozen cluster points only (live ring buffer unaffected).</summary>
    public void ClearFrozenPoints()
    {
        lock (_dataLock)
        {
            _frozenPoints = Array.Empty<Point3D>();
            _frozenClasses = Array.Empty<int>();
            _frozenCount = 0;
        }
        _needsRender = true;
    }

    /// <summary>Clear everything — live ring buffer and frozen clusters.</summary>
    public void Clear()
    {
        lock (_dataLock)
        {
            _ringCount = 0;
            _ringWrite = 0;
            _frozenPoints = Array.Empty<Point3D>();
            _frozenClasses = Array.Empty<int>();
            _frozenCount = 0;
        }
        PointCount = 0;
        HasData = false;
        ResetBounds();
        _needsRender = true;
    }

    /// <summary>Clear the live ring buffer only (frozen clusters unaffected).</summary>
    public void ClearLive()
    {
        lock (_dataLock)
        {
            _ringCount = 0;
            _ringWrite = 0;
        }
        _needsRender = true;
    }

    /// <summary>Maximum points in the ring buffer. Setting this resizes the buffer, keeping the newest points.</summary>
    public int MaxPoints
    {
        get => _maxPoints;
        set
        {
            int newMax = Math.Max(10, value);
            if (newMax == _maxPoints) return;
            lock (_dataLock)
            {
                var (pts, cls, count) = SnapshotPoints();

                _maxPoints = newMax;
                _ringBuffer = new Point3D[newMax];
                _ringClasses = new int[newMax];

                int copyCount = Math.Min(count, newMax);
                int srcStart = count - copyCount;
                for (int i = 0; i < copyCount; i++)
                {
                    _ringBuffer[i] = pts[srcStart + i];
                    _ringClasses[i] = cls != null ? cls[srcStart + i] : 0;
                }
                _ringWrite = copyCount % newMax;
                _ringCount = copyCount;
            }
            _needsRender = true;
            PointCount = GetPointCount();
        }
    }

    private int GetPointCount()
    {
        lock (_dataLock) return _ringCount;
    }

    /// <summary>
    /// Allocating snapshot of the ring buffer — used only by the <see cref="MaxPoints"/> resize
    /// path, never per frame. Caller must hold <see cref="_dataLock"/>.
    /// </summary>
    private (Point3D[] pts, int[] cls, int count) SnapshotPoints()
    {
        var count = _ringCount;
        if (count == 0) return (Array.Empty<Point3D>(), Array.Empty<int>(), 0);

        var pts = new Point3D[count];
        var cls = new int[count];
        int start = (_ringWrite - count + _maxPoints) % _maxPoints;
        for (int i = 0; i < count; i++)
        {
            int idx = (start + i) % _maxPoints;
            pts[i] = _ringBuffer[idx];
            cls[i] = _ringClasses[idx];
        }
        return (pts, cls, count);
    }

    #endregion

    #region Bounds

    private void UpdateBounds(Point3D[] points, int count)
    {
        double mnX = double.MaxValue, mxX = double.MinValue;
        double mnY = double.MaxValue, mxY = double.MinValue;
        double mnZ = double.MaxValue, mxZ = double.MinValue;

        for (int i = 0; i < count; i++)
        {
            var p = points[i];
            if (p.X < mnX) mnX = p.X; if (p.X > mxX) mxX = p.X;
            if (p.Y < mnY) mnY = p.Y; if (p.Y > mxY) mxY = p.Y;
            if (p.Z < mnZ) mnZ = p.Z; if (p.Z > mxZ) mxZ = p.Z;
        }

        // Minimum range per axis so early data doesn't create tiny jumpy axes.
        const double minRange = 1.0;
        EnsureMinRange(ref mnX, ref mxX, minRange);
        EnsureMinRange(ref mnY, ref mxY, minRange);
        EnsureMinRange(ref mnZ, ref mxZ, minRange);

        // Snap outward to "nice" multiples — prevents micro-rescale jitter.
        SnapBounds(ref mnX, ref mxX);
        SnapBounds(ref mnY, ref mxY);
        SnapBounds(ref mnZ, ref mxZ);

        if (!_boundsInitialized)
        {
            _minX = mnX; _maxX = mxX;
            _minY = mnY; _maxY = mxY;
            _minZ = mnZ; _maxZ = mxZ;
            _boundsInitialized = true;
        }
        else
        {
            // Expand-only — keeps the view completely stable.
            if (mnX < _minX) _minX = mnX;
            if (mxX > _maxX) _maxX = mxX;
            if (mnY < _minY) _minY = mnY;
            if (mxY > _maxY) _maxY = mxY;
            if (mnZ < _minZ) _minZ = mnZ;
            if (mxZ > _maxZ) _maxZ = mxZ;
        }
    }

    private static void EnsureMinRange(ref double lo, ref double hi, double minRange)
    {
        double r = hi - lo;
        if (r < minRange) { double mid = (lo + hi) / 2; lo = mid - minRange / 2; hi = mid + minRange / 2; }
    }

    /// <summary>Snap bounds outward to "nice" round numbers so they don't jitter.</summary>
    private static void SnapBounds(ref double lo, ref double hi)
    {
        double range = hi - lo;
        if (range <= 0) return;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(range)));
        double norm = range / mag;
        double step = (norm < 2 ? 0.2 : norm < 5 ? 0.5 : 1.0) * mag;
        lo = Math.Floor(lo / step) * step;
        hi = Math.Ceiling(hi / step) * step;
    }

    private void ResetBounds()
    {
        _boundsInitialized = false;
        _minX = _maxX = _minY = _maxY = _minZ = _maxZ = 0;
    }

    #endregion

    #region Rendering

    private void OnTimerTick()
    {
        if (_disposed || !_isActive || !_needsRender) return;
        if (Interlocked.CompareExchange(ref _renderScheduled, 1, 0) != 0) return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _needsRender = false;
                Render();
            }
            finally
            {
                Interlocked.Exchange(ref _renderScheduled, 0);
            }
        }, DispatcherPriority.Render);
    }

    private void EnsurePointCapacity(int n)
    {
        if (_pts.Length >= n) return;
        int cap = Math.Max(n, _pts.Length == 0 ? 256 : _pts.Length * 2);
        _pts = new Point3D[cap];
        _cls = new int[cap];
        _projSx = new float[cap];
        _projSy = new float[cap];
        _depthKey = new float[cap];
        _order = new int[cap];
    }

    private void EnsureBitmaps()
    {
        if (_wbA != null) return;
        var size = new PixelSize(BitmapWidth, BitmapHeight);
        var dpi = new Vector(96, 96);
        _wbA = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        _wbB = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
    }

    private void Render()
    {
        int frozenCount, liveCount, totalCount;

        lock (_dataLock)
        {
            frozenCount = _frozenCount;
            liveCount = _ringCount;
            totalCount = frozenCount + liveCount;
            if (totalCount == 0) return;

            EnsurePointCapacity(totalCount);

            // Copy frozen-first, then the live ring, straight into the reused merged buffers.
            if (frozenCount > 0)
            {
                Array.Copy(_frozenPoints, 0, _pts, 0, frozenCount);
                Array.Copy(_frozenClasses, 0, _cls, 0, frozenCount);
            }
            if (liveCount > 0)
            {
                int start = (_ringWrite - liveCount + _maxPoints) % _maxPoints;
                int first = Math.Min(liveCount, _maxPoints - start);
                Array.Copy(_ringBuffer, start, _pts, frozenCount, first);
                Array.Copy(_ringClasses, start, _cls, frozenCount, first);
                if (liveCount > first)
                {
                    Array.Copy(_ringBuffer, 0, _pts, frozenCount + first, liveCount - first);
                    Array.Copy(_ringClasses, 0, _cls, frozenCount + first, liveCount - first);
                }
            }
        }

        UpdateBounds(_pts, totalCount);
        EnsureBitmaps();

        var target = _useA ? _wbA! : _wbB!;
        using (var fb = target.Lock())
        {
            var info = new SKImageInfo(BitmapWidth, BitmapHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, fb.Address, fb.RowBytes);
            DrawScene(surface.Canvas, totalCount, frozenCount, liveCount);
        }

        Image = target;       // ref swap → binding updates, no encode/decode
        _useA = !_useA;
    }

    private void DrawScene(SKCanvas canvas, int totalCount, int frozenCount, int liveCount)
    {
        canvas.Clear(BackgroundColor);

        var proj = new Projector(_minX, _maxX, _minY, _maxY, _minZ, _maxZ, Azimuth, Elevation, Zoom);

        // Project once into the reused arrays, then depth-sort with primitive keys (painter's algorithm).
        for (int i = 0; i < totalCount; i++)
        {
            var (sx, sy, d) = proj.Project(_pts[i].X, _pts[i].Y, _pts[i].Z);
            _projSx[i] = sx;
            _projSy[i] = sy;
            _depthKey[i] = (float)d;
            _order[i] = i;
        }
        Array.Sort(_depthKey, _order, 0, totalCount);

        DrawPoints(canvas, totalCount, frozenCount, liveCount);
        DrawCentroids(canvas, in proj, frozenCount);
        DrawAxes(canvas, in proj);
        DrawLegend(canvas, totalCount);
        DrawInfo(canvas, frozenCount, liveCount);
    }

    private void DrawPoints(SKCanvas canvas, int totalCount, int frozenCount, int liveCount)
    {
        for (int k = 0; k < totalCount; k++)
        {
            int idx = _order[k];
            float sx = _projSx[idx], sy = _projSy[idx];
            if (sx < 0 || sx > BitmapWidth || sy < 0 || sy > BitmapHeight) continue;

            double depth = _depthKey[k];
            var baseColor = GetClassColor(_cls[idx]);

            if (idx < frozenCount)
                DrawFrozenPoint(canvas, sx, sy, depth, baseColor, frozenCount);
            else
                DrawLivePoint(canvas, sx, sy, depth, baseColor, idx - frozenCount, liveCount);
        }
    }

    private void DrawFrozenPoint(SKCanvas canvas, float sx, float sy, double depth, SKColor baseColor, int frozenCount)
    {
        float depthT = (float)Math.Clamp((depth + 1) / 2.0, 0, 1);
        float baseRadius = frozenCount > 2000 ? 1.5f : frozenCount > 500 ? 2.5f : 3.5f;
        float radius = baseRadius + depthT * 1.5f;

        _pointPaint.Color = baseColor.WithAlpha((byte)((0.15f + depthT * 0.65f) * 255));
        canvas.DrawCircle(sx, sy, radius, _pointPaint);

        if (depthT > 0.3f)
        {
            byte er = (byte)(baseColor.Red * 0.5f);
            byte eg = (byte)(baseColor.Green * 0.5f);
            byte eb = (byte)(baseColor.Blue * 0.5f);
            _edgePaint.Color = new SKColor(er, eg, eb, (byte)((depthT - 0.3f) * 0.5f * 255));
            canvas.DrawCircle(sx, sy, radius, _edgePaint);
        }
    }

    private void DrawLivePoint(SKCanvas canvas, float sx, float sy, double depth, SKColor baseColor, int liveIdx, int liveCount)
    {
        float recency = liveCount > 1 ? (float)liveIdx / (liveCount - 1) : 1f;
        float baseR = liveCount > 2000 ? 1.5f : liveCount > 500 ? 2.0f : 3.0f;
        float radius = baseR * (0.5f + recency * 0.5f);

        float depthFactor = (float)Math.Clamp(0.5 + 0.5 * ((depth + 1) / 2.0), 0.4, 1.0);
        float alpha = (0.10f + recency * 0.85f) * depthFactor;
        float fade = Math.Clamp(recency * 1.2f, 0, 1);

        byte r = (byte)(0x1A + (baseColor.Red - 0x1A) * fade);
        byte g = (byte)(0x1A + (baseColor.Green - 0x1A) * fade);
        byte b = (byte)(0x2E + (baseColor.Blue - 0x2E) * fade);

        _pointPaint.Color = new SKColor(r, g, b, (byte)(alpha * 255));
        canvas.DrawCircle(sx, sy, radius, _pointPaint);

        if (recency > 0.95f)
        {
            float glowT = (recency - 0.95f) / 0.05f;

            _pointPaint.Color = new SKColor(0xAA, 0xEE, 0xFF, (byte)(glowT * 0.12f * depthFactor * 255));
            canvas.DrawCircle(sx, sy, radius * 2.5f, _pointPaint);

            _pointPaint.Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)(glowT * 0.5f * depthFactor * 255));
            canvas.DrawCircle(sx, sy, radius * 0.6f, _pointPaint);
        }
    }

    private void DrawCentroids(SKCanvas canvas, in Projector proj, int frozenCount)
    {
        _centroidSums.Clear();
        for (int i = 0; i < frozenCount; i++)
        {
            int ci = _cls[i];
            _centroidSums.TryGetValue(ci, out var acc);
            _centroidSums[ci] = (acc.sx + _pts[i].X, acc.sy + _pts[i].Y, acc.sz + _pts[i].Z, acc.count + 1);
        }

        float dSize = 6f + (float)Math.Clamp(Zoom - 1, 0, 3) * 2f;
        foreach (var (ci, (csx, csy, csz, cc)) in _centroidSums)
        {
            if (cc == 0) continue;
            var (pcx, pcy, _) = proj.Project(csx / cc, csy / cc, csz / cc);

            _scratchPath.Reset();
            _scratchPath.MoveTo(pcx, pcy - dSize);
            _scratchPath.LineTo(pcx + dSize, pcy);
            _scratchPath.LineTo(pcx, pcy + dSize);
            _scratchPath.LineTo(pcx - dSize, pcy);
            _scratchPath.Close();

            _centroidPaint.Color = GetClassColor(ci).WithAlpha(0xDD);
            canvas.DrawPath(_scratchPath, _centroidPaint);
            _centroidEdgePaint.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x90);
            canvas.DrawPath(_scratchPath, _centroidEdgePaint);
        }
    }

    private void DrawAxes(SKCanvas canvas, in Projector proj)
    {
        double midX = (_minX + _maxX) / 2;
        double midY = (_minY + _maxY) / 2;
        double midZ = (_minZ + _maxZ) / 2;

        double rangeX = _maxX - _minX, rangeY = _maxY - _minY, rangeZ = _maxZ - _minZ;
        double extX = rangeX * 0.2, extY = rangeY * 0.2, extZ = rangeZ * 0.2;

        var (ox, oy, _) = proj.Project(midX, midY, midZ);

        // Subtle floor grid on the XZ plane at Y=min.
        const int gridN = 5;
        double floorY = _minY - extY * 0.3;
        for (int gi = 0; gi <= gridN; gi++)
        {
            float t = gi / (float)gridN;
            double gx = (_minX - extX * 0.2) + (rangeX + extX * 0.4) * t;
            double gz = (_minZ - extZ * 0.2) + (rangeZ + extZ * 0.4) * t;
            var (g1x, g1y, _) = proj.Project(gx, floorY, _minZ - extZ * 0.2);
            var (g2x, g2y, _) = proj.Project(gx, floorY, _maxZ + extZ * 0.2);
            canvas.DrawLine(g1x, g1y, g2x, g2y, _gridPaint);
            var (g3x, g3y, _) = proj.Project(_minX - extX * 0.2, floorY, gz);
            var (g4x, g4y, _) = proj.Project(_maxX + extX * 0.2, floorY, gz);
            canvas.DrawLine(g3x, g3y, g4x, g4y, _gridPaint);
        }

        canvas.DrawCircle(ox, oy, 3, _originPaint);

        var axes = new[]
        {
            (negX: _minX - extX, negY: midY,         negZ: midZ,
             posX: _maxX + extX, posY: midY,         posZ: midZ,
             color: new SKColor(0xFF, 0x88, 0x88), label: XLabel),
            (negX: midX,         negY: _minY - extY, negZ: midZ,
             posX: midX,         posY: _maxY + extY, posZ: midZ,
             color: new SKColor(0x88, 0xFF, 0x88), label: YLabel),
            (negX: midX,         negY: midY,         negZ: _minZ - extZ,
             posX: midX,         posY: midY,         posZ: _maxZ + extZ,
             color: new SKColor(0x88, 0xAA, 0xFF), label: ZLabel),
        };

        foreach (var (nx, ny, nz, px, py, pz, color, label) in axes)
        {
            var (p0x, p0y, _) = proj.Project(nx, ny, nz);
            var (p1x, p1y, _) = proj.Project(px, py, pz);

            _negPaint.Color = color.WithAlpha(0x50);
            canvas.DrawLine(p0x, p0y, ox, oy, _negPaint);

            _axisPaint.Color = color.WithAlpha(0xCC);
            canvas.DrawLine(ox, oy, p1x, p1y, _axisPaint);

            float adx = p1x - p0x, ady = p1y - p0y;
            float alen = MathF.Sqrt(adx * adx + ady * ady);
            if (alen <= 10) continue;

            float ux = adx / alen, uy = ady / alen;
            _arrowPaint.Color = color;
            _scratchPath.Reset();
            _scratchPath.MoveTo(p1x, p1y);
            _scratchPath.LineTo(p1x - ux * 12 + uy * 6, p1y - uy * 12 - ux * 6);
            _scratchPath.LineTo(p1x - ux * 12 - uy * 6, p1y - uy * 12 + ux * 6);
            _scratchPath.Close();
            canvas.DrawPath(_scratchPath, _arrowPaint);

            _labelPaint.Color = color;
            float lx = p1x + ux * 18;
            float ly = p1y + uy * 18 + 7;
            float lw = _labelPaint.MeasureText(label);
            canvas.DrawRoundRect(lx - lw / 2 - 5, ly - 18, lw + 10, 24, 12, 12, _labelBgPaint);
            canvas.DrawText(label, lx - lw / 2, ly, _labelPaint);
        }
    }

    private void DrawLegend(SKCanvas canvas, int totalCount)
    {
        _legendCounts.Clear();
        for (int i = 0; i < totalCount; i++)
        {
            int c = _cls[i];
            _legendCounts.TryGetValue(c, out var n);
            _legendCounts[c] = n + 1;
        }
        if (_legendCounts.Count <= 1) return;

        const float legendX = 12;
        const float dotR = 5f;
        const float rowH = 20f;
        float legendY = BitmapHeight - 12;

        _legendKeys.Clear();
        _legendKeys.AddRange(_legendCounts.Keys);
        _legendKeys.Sort();

        float totalH = _legendKeys.Count * rowH + 8;
        canvas.DrawRoundRect(legendX - 4, legendY - totalH + 4, 120, totalH, 6, 6, _legendBgPaint);

        for (int i = _legendKeys.Count - 1; i >= 0; i--)
        {
            int ci = _legendKeys[i];
            _legendDotPaint.Color = GetClassColor(ci);
            canvas.DrawCircle(legendX + dotR, legendY - dotR, dotR, _legendDotPaint);
            canvas.DrawText($"Class {ci}  ({_legendCounts[ci]})", legendX + dotR * 2 + 8, legendY, _legendTextPaint);
            legendY -= rowH;
        }
    }

    private void DrawInfo(SKCanvas canvas, int frozenCount, int liveCount)
    {
        string info = frozenCount > 0 ? $"{frozenCount} clusters  {liveCount} live" : $"{liveCount} pts";
        float iw = _infoPaint.MeasureText(info);
        canvas.DrawText(info, BitmapWidth - iw - 12, 22, _infoPaint);
    }

    /// <summary>Orthographic camera: normalizes data to a cube, rotates by azimuth/elevation, projects to screen.</summary>
    private readonly struct Projector
    {
        private readonly double _midX, _midY, _midZ, _invRange;
        private readonly double _cosAz, _sinAz, _cosEl, _sinEl;
        private readonly float _cx, _cy, _halfPlot;

        public Projector(double minX, double maxX, double minY, double maxY, double minZ, double maxZ,
                         double azimuthDeg, double elevationDeg, double zoom)
        {
            const double dataMargin = 1.35;
            double maxRange = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            _midX = (minX + maxX) / 2;
            _midY = (minY + maxY) / 2;
            _midZ = (minZ + maxZ) / 2;
            _invRange = maxRange > 0 ? 1.0 / (maxRange * dataMargin) : 1.0;

            double az = azimuthDeg * Math.PI / 180.0;
            double el = elevationDeg * Math.PI / 180.0;
            _cosAz = Math.Cos(az); _sinAz = Math.Sin(az);
            _cosEl = Math.Cos(el); _sinEl = Math.Sin(el);

            float plotSize = Math.Min(BitmapWidth, BitmapHeight) - 2 * Padding;
            _cx = BitmapWidth / 2f;
            _cy = BitmapHeight / 2f;
            _halfPlot = plotSize / 2f * (float)Math.Clamp(zoom, 0.2, 10.0);
        }

        public (float sx, float sy, double depth) Project(double x, double y, double z)
        {
            double nx = (x - _midX) * _invRange * 2;
            double ny = (y - _midY) * _invRange * 2;
            double nz = (z - _midZ) * _invRange * 2;

            double px = nx * _cosAz + nz * _sinAz;
            double py = -nx * _sinAz * _sinEl + ny * _cosEl + nz * _cosAz * _sinEl;
            double pz = -nx * _sinAz * _cosEl - ny * _sinEl + nz * _cosAz * _cosEl;

            return (_cx + (float)(px * _halfPlot), _cy - (float)(py * _halfPlot), pz);
        }
    }

    #endregion
}

/// <summary>A point in 3D space.</summary>
public readonly record struct Point3D(double X, double Y, double Z);