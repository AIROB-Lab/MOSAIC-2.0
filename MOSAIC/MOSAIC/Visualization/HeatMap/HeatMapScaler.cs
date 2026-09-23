using System;
using LiveChartsCore.Defaults;

namespace MOSAIC.Visualization.Heatmap;

/// <summary>
/// Legend text returned by a scaler for the colorbar.
/// </summary>
public struct LegendInfo
{
    public string MinLabel;
    public string MaxLabel;
    public string Caption;
}

/// <summary>
/// Maps raw values to color weights (0..1) for a heatmap frame and provides legend text.
/// </summary>
public interface IHeatmapScaler
{
    /// <summary>
    /// Called whenever the grid size changes (length = rows*cols).
    /// Implementations must handle any cell count gracefully — never throw.
    /// </summary>
    void Configure(int cellCount);

    /// <summary>
    /// Map an input frame to weights in <see cref="WeightedPoint.Weight"/>.
    /// </summary>
    void MapFrame(
        ReadOnlySpan<double> frame,
        WeightedPoint[] points,
        int[]? indexMap,
        Func<double, double>? valueTransform,
        out LegendInfo legend);
}

// ═══════════════════════════════════════════════════════════════════════════════
// 1) Global auto-range — expand fast, contract slow
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Global auto-range scaler with hysteresis (expand fast, contract slow).
/// Ideal when the signal range is unknown or drifting.
/// Handles grid resize gracefully — optionally preserves or resets range.
/// </summary>
public sealed class GlobalAutoScaler : IHeatmapScaler
{
    private double _gMin = double.NaN, _gMax = double.NaN;
    private readonly double _expandA, _contractA, _minSpan;
    private readonly bool _resetOnReconfigure;

    /// <param name="expandAlpha">Weight when range expands (0..1). Higher = faster tracking.</param>
    /// <param name="contractAlpha">Weight when range contracts (0..1). Lower = steadier baseline.</param>
    /// <param name="minSpan">Minimum span to avoid numeric collapse.</param>
    /// <param name="resetOnReconfigure">If true, resets range on Configure(). If false, preserves range across grid changes.</param>
    public GlobalAutoScaler(
        double expandAlpha = 0.6,
        double contractAlpha = 0.08,
        double minSpan = 1e-9,
        bool resetOnReconfigure = true)
    {
        _expandA = expandAlpha;
        _contractA = contractAlpha;
        _minSpan = minSpan;
        _resetOnReconfigure = resetOnReconfigure;
    }

    public void Configure(int cellCount)
    {
        if (_resetOnReconfigure)
        {
            _gMin = double.NaN;
            _gMax = double.NaN;
        }
    }

    public void MapFrame(
        ReadOnlySpan<double> frame,
        WeightedPoint[] points,
        int[]? indexMap,
        Func<double, double>? valueTransform,
        out LegendInfo legend)
    {
        double fmin = double.PositiveInfinity, fmax = double.NegativeInfinity;

        for (int i = 0; i < frame.Length; i++)
        {
            double v = frame[i];
            if (!double.IsFinite(v)) continue;
            if (valueTransform is not null) v = valueTransform(v);
            if (v < fmin) fmin = v;
            if (v > fmax) fmax = v;
        }

        // If frame was entirely non-finite, keep previous range or use 0..1
        if (!double.IsFinite(fmin) || !double.IsFinite(fmax))
        {
            fmin = double.IsNaN(_gMin) ? 0 : _gMin;
            fmax = double.IsNaN(_gMax) ? 1 : _gMax;
        }

        if (double.IsNaN(_gMin) || double.IsNaN(_gMax))
        {
            _gMin = fmin;
            _gMax = fmax;
        }
        else
        {
            _gMin += (fmin < _gMin ? _expandA : _contractA) * (fmin - _gMin);
            _gMax += (fmax > _gMax ? _expandA : _contractA) * (fmax - _gMax);
        }

        double rmin = _gMin, rmax = _gMax;
        double span = rmax - rmin;

        if (!(span > 0))
        {
            double mid = 0.5 * (rmin + rmax);
            rmin = mid - 0.5 * _minSpan;
            rmax = mid + 0.5 * _minSpan;
            span = _minSpan;
        }

        double inv = 1.0 / span;

        for (int i = 0; i < frame.Length; i++)
        {
            int dst = indexMap is null ? i : (i < indexMap.Length ? indexMap[i] : -1);
            if ((uint)dst >= (uint)points.Length) continue;

            double v = frame[i];
            if (!double.IsFinite(v)) v = rmin;
            if (valueTransform is not null) v = valueTransform(v);

            double w = (v - rmin) * inv;
            points[dst].Weight = w < 0 ? 0 : w > 1 ? 1 : w;
        }

        legend = new LegendInfo
        {
            MinLabel = FormatValue(rmin),
            MaxLabel = FormatValue(rmax),
            Caption = "auto"
        };
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
}

// ═══════════════════════════════════════════════════════════════════════════════
// 2) Global fixed-range — known min/max for all cells
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Global fixed-range scaler. Maps [min..max] → [0..1] for all cells uniformly.
/// Ideal for known signal ranges (e.g., ±1V, 0..100%).
/// Handles grid resize with no issues.
/// </summary>
public sealed class GlobalFixedScaler : IHeatmapScaler
{
    private readonly double _min, _max, _eps;

    public GlobalFixedScaler(double min, double max, double epsilon = 1e-9)
    {
        if (!(min < max)) throw new ArgumentException("min must be < max");
        _min = min;
        _max = max;
        _eps = epsilon;
    }

    public void Configure(int cellCount) { /* nothing to do */ }

    public void MapFrame(
        ReadOnlySpan<double> frame,
        WeightedPoint[] points,
        int[]? indexMap,
        Func<double, double>? valueTransform,
        out LegendInfo legend)
    {
        double span = Math.Max(_max - _min, _eps);
        double inv = 1.0 / span;

        for (int i = 0; i < frame.Length; i++)
        {
            int dst = indexMap is null ? i : (i < indexMap.Length ? indexMap[i] : -1);
            if ((uint)dst >= (uint)points.Length) continue;

            double v = frame[i];
            if (!double.IsFinite(v)) v = _min;
            if (valueTransform is not null) v = valueTransform(v);

            double w = (v - _min) * inv;
            points[dst].Weight = w < 0 ? 0 : w > 1 ? 1 : w;
        }

        legend = new LegendInfo
        {
            MinLabel = _min.ToString("G4"),
            MaxLabel = _max.ToString("G4"),
            Caption = "fixed"
        };
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 3) Per-sensor fixed-range — each cell has its own [min,max]
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Per-sensor fixed ranges; each cell maps its own [min,max] to [0..1].
/// Handles grid resize gracefully:
///   - If new count matches arrays → use them directly.
///   - If new count differs → broadcasts overall [globalMin, globalMax] to all cells.
/// This means you never crash on channel count changes, just get a less specific scaling.
/// </summary>
public sealed class PerSensorFixedScaler : IHeatmapScaler
{
    private double[] _min;
    private double[] _max;
    private readonly double _eps;
    private double _overallMin, _overallMax;
    private int _configuredCount;

    public PerSensorFixedScaler(double[] mins, double[] maxs, double epsilon = 1e-9)
    {
        if (mins is null || maxs is null || mins.Length != maxs.Length)
            throw new ArgumentException("mins/maxs length mismatch.");

        _min = (double[])mins.Clone();
        _max = (double[])maxs.Clone();
        _eps = epsilon;
        ComputeOverallBounds();
        _configuredCount = mins.Length;
    }

    public void Configure(int cellCount)
    {
        _configuredCount = cellCount;

        if (cellCount == _min.Length)
        {
            // Exact match — use per-sensor arrays as-is
            ComputeOverallBounds();
            return;
        }

        // Cell count changed — broadcast overall bounds to all cells.
        // This is a graceful fallback: we lose per-sensor specificity
        // but avoid crashing. Callers can re-set per-sensor arrays later.
        var newMin = new double[cellCount];
        var newMax = new double[cellCount];

        double gMin = double.IsFinite(_overallMin) ? _overallMin : -1;
        double gMax = double.IsFinite(_overallMax) ? _overallMax : 1;

        for (int i = 0; i < cellCount; i++)
        {
            // Copy from original if available, otherwise broadcast overall bounds
            if (i < _min.Length)
            {
                newMin[i] = _min[i];
                newMax[i] = _max[i];
            }
            else
            {
                newMin[i] = gMin;
                newMax[i] = gMax;
            }
        }

        _min = newMin;
        _max = newMax;
        ComputeOverallBounds();
    }

    private void ComputeOverallBounds()
    {
        double glMin = double.PositiveInfinity, glMax = double.NegativeInfinity;
        for (int i = 0; i < _min.Length; i++)
        {
            if (_min[i] < glMin) glMin = _min[i];
            if (_max[i] > glMax) glMax = _max[i];
        }
        _overallMin = glMin;
        _overallMax = glMax;
    }

    public void MapFrame(
        ReadOnlySpan<double> frame,
        WeightedPoint[] points,
        int[]? indexMap,
        Func<double, double>? valueTransform,
        out LegendInfo legend)
    {
        for (int i = 0; i < frame.Length; i++)
        {
            int dst = indexMap is null ? i : (i < indexMap.Length ? indexMap[i] : -1);
            if ((uint)dst >= (uint)points.Length) continue;

            double v = frame[i];
            if (!double.IsFinite(v)) v = 0;
            if (valueTransform is not null) v = valueTransform(v);

            // Use per-sensor range if within bounds, otherwise overall bounds
            double lo = dst < _min.Length ? _min[dst] : _overallMin;
            double hi = dst < _max.Length ? _max[dst] : _overallMax;
            double span = Math.Max(hi - lo, _eps);

            double w = (v - lo) / span;
            points[dst].Weight = w < 0 ? 0 : w > 1 ? 1 : w;
        }

        legend = new LegendInfo
        {
            MinLabel = _overallMin.ToString("G4"),
            MaxLabel = _overallMax.ToString("G4"),
            Caption = "per-sensor"
        };
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 4) Per-sensor adaptive RMS — auto-scaling per cell
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Per-sensor adaptive RMS scaler with peak-hold decay.
/// Zero-centered mapping: [-scale .. +scale] → [0..1].
/// Each cell tracks its own RMS envelope and auto-scales independently.
/// Handles grid resize by re-allocating state (new cells start fresh).
/// </summary>
public sealed class PerSensorAdaptiveRmsScaler : IHeatmapScaler
{
    private double[]? _emaSq;
    private double[]? _scale;
    private int _configuredCount;
    private DateTime _last = DateTime.UtcNow;

    /// <summary>EMA smoothing factor (0.05..0.3). Higher = faster envelope tracking.</summary>
    public double EmaAlpha { get; set; } = 0.12;

    /// <summary>Scale multiplier relative to RMS. √2 clips sine peaks at ±1.</summary>
    public double K { get; set; } = Math.Sqrt(2);

    /// <summary>Minimum scale to avoid divide-by-zero.</summary>
    public double Epsilon { get; set; } = 1e-9;

    /// <summary>Peak-hold decay half-life in seconds. Shorter = faster re-centering.</summary>
    public double HalfLifeSeconds { get; set; } = 0.6;

    public void Configure(int cellCount)
    {
        if (cellCount == _configuredCount && _emaSq is not null && _scale is not null)
            return;

        // Preserve state for existing cells, initialize new ones fresh
        var oldEmaSq = _emaSq;
        var oldScale = _scale;
        int oldCount = _configuredCount;

        _emaSq = new double[cellCount];
        _scale = new double[cellCount];

        for (int i = 0; i < cellCount; i++)
        {
            if (oldEmaSq is not null && oldScale is not null && i < oldCount)
            {
                _emaSq[i] = oldEmaSq[i];
                _scale[i] = oldScale[i];
            }
            else
            {
                _emaSq[i] = double.NaN;
                _scale[i] = 0.0;
            }
        }

        _configuredCount = cellCount;
        _last = DateTime.UtcNow;
    }

    public void MapFrame(
        ReadOnlySpan<double> frame,
        WeightedPoint[] points,
        int[]? indexMap,
        Func<double, double>? valueTransform,
        out LegendInfo legend)
    {
        if (_emaSq is null || _scale is null || _configuredCount == 0)
        {
            legend = new LegendInfo { MinLabel = "-1", MaxLabel = "1", Caption = "per-sensor (init)" };
            return;
        }

        var now = DateTime.UtcNow;
        double dt = Math.Max(1e-3, (now - _last).TotalSeconds);
        _last = now;

        double decay = Math.Exp(-Math.Log(2) * dt / Math.Max(1e-3, HalfLifeSeconds));
        double a = EmaAlpha, k = K;

        double maxScale = 0; // track for legend

        for (int i = 0; i < frame.Length; i++)
        {
            int dst = indexMap is null ? i : (i < indexMap.Length ? indexMap[i] : -1);
            if ((uint)dst >= (uint)points.Length) continue;
            if (dst >= _configuredCount) continue;

            double v = frame[i];
            if (!double.IsFinite(v)) v = 0;
            if (valueTransform is not null) v = valueTransform(v);

            // EMA of squared value → RMS envelope
            double vsq = v * v;
            if (double.IsNaN(_emaSq[dst])) _emaSq[dst] = vsq;
            else _emaSq[dst] = (1 - a) * _emaSq[dst] + a * vsq;

            double rms = Math.Sqrt(Math.Max(_emaSq[dst], 0.0));
            double target = Math.Max(rms * k, Epsilon);

            // peak-hold + decay
            _scale[dst] = Math.Max(target, _scale[dst] * decay);
            double s = Math.Max(_scale[dst], Epsilon);

            if (s > maxScale) maxScale = s;

            // map [-s .. +s] → [0..1]
            double z = v / s;
            points[dst].Weight = 0.5 + 0.5 * Math.Clamp(z, -1, 1);
        }

        legend = new LegendInfo
        {
            MinLabel = maxScale > 0.01 ? $"-{maxScale:G3}" : "-rms",
            MaxLabel = maxScale > 0.01 ? $"+{maxScale:G3}" : "+rms",
            Caption = "adaptive"
        };
    }
}