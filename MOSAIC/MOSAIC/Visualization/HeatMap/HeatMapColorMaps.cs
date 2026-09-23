using System;
using System.Linq;
using SkiaSharp;

namespace MOSAIC.Visualization.Heatmap;

/// <summary>
/// Extension methods providing pre-built colormaps for <see cref="HeatMapMonitor"/>.
/// 
/// Sequential (low-to-high): Viridis, Inferno, Plasma, Magma, Turbo, Hot, Greyscale
/// Diverging (negative-zero-positive): BlueWhiteRed, CoolWarm, PurpleWhiteGreen
/// 
/// Usage:
///   heatmap.UseViridis();
///   heatmap.UseBlueWhiteRed();
///   heatmap.UseGreyscale();
/// </summary>
public static class HeatmapColormaps
{
    // ═══════════════════════════════════════════════════════════════════════
    // Sequential colormaps (perceptually uniform where possible)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Viridis — perceptually uniform, colorblind-friendly. Purple → teal → yellow.</summary>
    public static void UseViridis(this HeatMapMonitor hm, int levels = 256)
    {
        ApplyAnchored(hm, [
            "#440154", "#482878", "#3E4A89", "#31688E", "#26828E",
            "#1F9E89", "#35B779", "#6CCE59", "#B4DE2C", "#FDE725"
        ], Math.Max(levels, 2));
    }

    /// <summary>Inferno — perceptually uniform. Black → purple → orange → yellow.</summary>
    public static void UseInferno(this HeatMapMonitor hm, int levels = 256)
    {
        ApplyAnchored(hm, [
            "#000004", "#160B39", "#420A68", "#6A176E", "#932667",
            "#BC3754", "#DD513A", "#F37819", "#FCA50A", "#F0F921"
        ], Math.Max(levels, 2));
    }

    /// <summary>Plasma — perceptually uniform. Purple → pink → orange → yellow.</summary>
    public static void UsePlasma(this HeatMapMonitor hm, int levels = 256)
    {
        ApplyAnchored(hm, new[]
        {
            "#0D0887", "#3B049A", "#7201A8", "#A52C86", "#CC4778",
            "#ED6925", "#F89441", "#FDB42F", "#FACA2B", "#F0F921"
        }, Math.Max(levels, 2));
    }

    /// <summary>Magma — perceptually uniform. Black → purple → salmon → white.</summary>
    public static void UseMagma(this HeatMapMonitor hm, int levels = 256)
    {
        ApplyAnchored(hm, new[]
        {
            "#000004", "#140E36", "#3B0F70", "#641A80", "#8C2981",
            "#B73779", "#DE4968", "#F7705C", "#FE9F6D", "#FCFDBF"
        }, Math.Max(levels, 2));
    }

    /// <summary>Turbo — rainbow-like but more perceptually uniform than raw HSV. Blue → cyan → green → yellow → red.</summary>
    public static void UseTurbo(this HeatMapMonitor hm, int levels = 256)
    {
        ApplyAnchored(hm, new[]
        {
            "#30123B", "#4662D7", "#36AAF9", "#1AE4B6", "#72FE5E",
            "#C8EF34", "#FABA39", "#F66B19", "#CA2A04", "#7A0403"
        }, Math.Max(levels, 2));
    }

    /// <summary>Hot — black → red → yellow → white. Classic thermal camera look.</summary>
    public static void UseHot(this HeatMapMonitor hm, int levels = 256)
    {
        ApplyAnchored(hm, new[]
        {
            "#000000", "#4A0000", "#8B0000", "#CD0000", "#FF4500",
            "#FF8C00", "#FFD700", "#FFFF00", "#FFFF99", "#FFFFFF"
        }, Math.Max(levels, 2));
    }

    /// <summary>Rainbow — blue → cyan → green → yellow → red. HSV-based (not perceptually uniform).</summary>
    public static void UseRainbow(this HeatMapMonitor hm, int levels = 256)
    {
        levels = Math.Max(levels, 2);
        var colors = new SKColor[levels];
        for (int i = 0; i < levels; i++)
        {
            float t = i / (levels - 1f);
            float hue = 240f * (1f - t); // blue → red
            colors[i] = SKColor.FromHsv(hue, 100f, 100f);
        }
        hm.SetColormap(colors, null);
    }

    /// <summary>Greyscale — black → white. Simple and universally readable.</summary>
    public static void UseGreyscale(this HeatMapMonitor hm, int levels = 256)
    {
        levels = Math.Max(levels, 2);
        var colors = new SKColor[levels];
        for (int i = 0; i < levels; i++)
        {
            byte v = (byte)(i * 255 / (levels - 1));
            colors[i] = new SKColor(v, v, v);
        }
        hm.SetColormap(colors, null);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Diverging colormaps (centered, good for ±values)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Blue → white → red. Classic diverging colormap (RdBu style).</summary>
    public static void UseBlueWhiteRed(this HeatMapMonitor hm, int levels = 257)
    {
        ApplyAnchored(hm, new[]
        {
            "#2C7BB6", "#ABD9E9", "#FFFFBF", "#FDAE61", "#D7191C"
        }, Math.Max(levels, 3));
    }

    /// <summary>Cool blue → neutral grey → warm red. Moreland's CoolWarm — perceptually balanced diverging.</summary>
    public static void UseCoolWarm(this HeatMapMonitor hm, int levels = 257)
    {
        ApplyAnchored(hm, new[]
        {
            "#3B4CC0", "#6B8DE3", "#AECBF5", "#DDDDDD",
            "#F1A97E", "#DD6A4F", "#B40426"
        }, Math.Max(levels, 3));
    }

    /// <summary>Purple → white → green. Diverging, good for signed data with a meaningful zero.</summary>
    public static void UsePurpleWhiteGreen(this HeatMapMonitor hm, int levels = 257)
    {
        ApplyAnchored(hm, new[]
        {
            "#5E3C99", "#B2ABD2", "#FFFFFF", "#A6DBA0", "#1B7837"
        }, Math.Max(levels, 3));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Internal helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static void ApplyAnchored(HeatMapMonitor hm, string[] anchorHex, int levels)
    {
        var anchors = anchorHex.Select(SKColor.Parse).ToArray();
        var colors = new SKColor[levels];

        for (int i = 0; i < levels; i++)
        {
            float t = i / (levels - 1f);
            float p = t * (anchors.Length - 1);
            int a = (int)Math.Floor(p);
            int b = Math.Min(a + 1, anchors.Length - 1);
            float u = p - a;
            colors[i] = Lerp(anchors[a], anchors[b], u);
        }

        hm.SetColormap(colors, null);
    }

    private static SKColor Lerp(in SKColor c1, in SKColor c2, float t)
    {
        byte r = (byte)(c1.Red   + (c2.Red   - c1.Red)   * t);
        byte g = (byte)(c1.Green + (c2.Green - c1.Green) * t);
        byte b = (byte)(c1.Blue  + (c2.Blue  - c1.Blue)  * t);
        return new SKColor(r, g, b);
    }
}