using System;
using System.Linq;
using SkiaSharp;

namespace MOSAIC.Visualization.SpectrogramMonitor;

/// <summary>
/// Pre-built colormaps for <see cref="SpectrogramMonitor"/>, exposed as extension methods.
/// Equivalent palettes to <see cref="Heatmap.HeatmapColormaps"/>.
///
/// Sequential: Viridis, Inferno, Plasma, Magma, Turbo, Hot, Rainbow, Greyscale.
/// Diverging:  BlueWhiteRed, CoolWarm, PurpleWhiteGreen.
/// </summary>
/// <example>
/// <code>
/// spectrogram.UseViridis();
/// spectrogram.UseBlueWhiteRed();
/// </code>
/// </example>
public static class SpectrogramColormaps
{
    /// <summary>Viridis — perceptually uniform, colorblind-friendly. Purple → teal → yellow.</summary>
    public static void UseViridis(this SpectrogramMonitor sm, int levels = 256)
    {
        ApplyAnchored(sm, "Viridis", [
            "#440154", "#482878", "#3E4A89", "#31688E", "#26828E",
            "#1F9E89", "#35B779", "#6CCE59", "#B4DE2C", "#FDE725"
        ], Math.Max(levels, 2));
    }

    /// <summary>Inferno — perceptually uniform. Black → purple → orange → yellow.</summary>
    public static void UseInferno(this SpectrogramMonitor sm, int levels = 256)
    {
        ApplyAnchored(sm, "Inferno", [
            "#000004", "#160B39", "#420A68", "#6A176E", "#932667",
            "#BC3754", "#DD513A", "#F37819", "#FCA50A", "#F0F921"
        ], Math.Max(levels, 2));
    }

    /// <summary>Plasma — perceptually uniform. Purple → pink → orange → yellow.</summary>
    public static void UsePlasma(this SpectrogramMonitor sm, int levels = 256)
    {
        ApplyAnchored(sm, "Plasma", [
            "#0D0887", "#3B049A", "#7201A8", "#A52C86", "#CC4778",
            "#ED6925", "#F89441", "#FDB42F", "#FACA2B", "#F0F921"
        ], Math.Max(levels, 2));
    }

    /// <summary>Magma — perceptually uniform. Black → purple → salmon → white.</summary>
    public static void UseMagma(this SpectrogramMonitor sm, int levels = 256)
    {
        ApplyAnchored(sm, "Magma", [
            "#000004", "#140E36", "#3B0F70", "#641A80", "#8C2981",
            "#B73779", "#DE4968", "#F7705C", "#FE9F6D", "#FCFDBF"
        ], Math.Max(levels, 2));
    }

    /// <summary>Turbo — rainbow-like but more perceptually uniform than raw HSV. Blue → cyan → green → yellow → red.</summary>
    public static void UseTurbo(this SpectrogramMonitor sm, int levels = 256)
    {
        ApplyAnchored(sm, "Turbo", [
            "#30123B", "#4662D7", "#36AAF9", "#1AE4B6", "#72FE5E",
            "#C8EF34", "#FABA39", "#F66B19", "#CA2A04", "#7A0403"
        ], Math.Max(levels, 2));
    }

    /// <summary>Hot — black → red → yellow → white. Classic thermal-camera look.</summary>
    public static void UseHot(this SpectrogramMonitor sm, int levels = 256)
    {
        ApplyAnchored(sm, "Hot", [
            "#000000", "#4A0000", "#8B0000", "#CD0000", "#FF4500",
            "#FF8C00", "#FFD700", "#FFFF00", "#FFFF99", "#FFFFFF"
        ], Math.Max(levels, 2));
    }

    /// <summary>Rainbow — blue → cyan → green → yellow → red. HSV-based (not perceptually uniform).</summary>
    public static void UseRainbow(this SpectrogramMonitor sm, int levels = 256)
    {
        levels = Math.Max(levels, 2);
        var colors = new SKColor[levels];
        for (int i = 0; i < levels; i++)
        {
            float t = i / (levels - 1f);
            float hue = 240f * (1f - t);
            colors[i] = SKColor.FromHsv(hue, 100f, 100f);
        }
        sm.SetColormap(colors, "Rainbow");
    }

    /// <summary>Greyscale — black → white. Simple and universally readable.</summary>
    public static void UseGreyscale(this SpectrogramMonitor sm, int levels = 256)
    {
        levels = Math.Max(levels, 2);
        var colors = new SKColor[levels];
        for (int i = 0; i < levels; i++)
        {
            byte v = (byte)(i * 255 / (levels - 1));
            colors[i] = new SKColor(v, v, v);
        }
        sm.SetColormap(colors, "Greyscale");
    }

    /// <summary>Blue → white → red. Classic diverging colormap (RdBu style).</summary>
    public static void UseBlueWhiteRed(this SpectrogramMonitor sm, int levels = 257)
    {
        ApplyAnchored(sm, "B-W-R", [
            "#2C7BB6", "#ABD9E9", "#FFFFBF", "#FDAE61", "#D7191C"
        ], Math.Max(levels, 3));
    }

    /// <summary>Cool blue → neutral grey → warm red. Moreland's CoolWarm — perceptually balanced diverging.</summary>
    public static void UseCoolWarm(this SpectrogramMonitor sm, int levels = 257)
    {
        ApplyAnchored(sm, "CoolWarm", [
            "#3B4CC0", "#6B8DE3", "#AECBF5", "#DDDDDD",
            "#F1A97E", "#DD6A4F", "#B40426"
        ], Math.Max(levels, 3));
    }

    /// <summary>Purple → white → green. Diverging; good for signed data with a meaningful zero.</summary>
    public static void UsePurpleWhiteGreen(this SpectrogramMonitor sm, int levels = 257)
    {
        ApplyAnchored(sm, "P-W-G", [
            "#5E3C99", "#B2ABD2", "#FFFFFF", "#A6DBA0", "#1B7837"
        ], Math.Max(levels, 3));
    }

    private static void ApplyAnchored(SpectrogramMonitor sm, string name, string[] anchorHex, int levels)
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

        sm.SetColormap(colors, name);
    }

    private static SKColor Lerp(in SKColor c1, in SKColor c2, float t)
    {
        byte r = (byte)(c1.Red   + (c2.Red   - c1.Red)   * t);
        byte g = (byte)(c1.Green + (c2.Green - c1.Green) * t);
        byte b = (byte)(c1.Blue  + (c2.Blue  - c1.Blue)  * t);
        return new SKColor(r, g, b);
    }
}