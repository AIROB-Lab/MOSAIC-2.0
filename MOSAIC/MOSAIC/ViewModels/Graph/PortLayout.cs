using Avalonia;

namespace MOSAIC.ViewModels.Graph;

/// <summary>
/// Shared geometry for a block node's input/output ports, so the node view (which draws the port
/// dots) and <see cref="EdgeDrawableControl"/> (which anchors arrows to them) agree exactly.
/// The pipeline flows top→down: inputs sit on the top edge, the output on the bottom edge.
/// Coordinates are canvas-space; the fallback size matches <see cref="VertexViewModel"/> defaults.
/// </summary>
public static class PortLayout
{
    public const double Diameter = 16;
    public const double Radius = Diameter / 2;

    private const double FallbackWidth = 132;
    private const double FallbackHeight = 45;

    /// <summary>Canvas-space anchor of input port <paramref name="index"/> (of <paramref name="count"/>
    /// total) on the top edge of <paramref name="v"/>, evenly distributed left-to-right.</summary>
    public static Point InputAnchor(VertexViewModel v, int index, int count)
    {
        double w = v.Width > 0 ? v.Width : FallbackWidth;
        double x = v.X + w * (index + 1) / (count + 1);
        return new Point(x, v.Y);
    }

    /// <summary>Canvas-space anchor of the output port on the bottom edge of <paramref name="v"/>.</summary>
    public static Point OutputAnchor(VertexViewModel v)
    {
        double w = v.Width > 0 ? v.Width : FallbackWidth;
        double h = v.Height > 0 ? v.Height : FallbackHeight;
        return new Point(v.X + w / 2, v.Y + h);
    }

    /// <summary>Centre of <paramref name="v"/> (fallback anchor when a block has no input ports).</summary>
    public static Point Center(VertexViewModel v)
    {
        double w = v.Width > 0 ? v.Width : FallbackWidth;
        double h = v.Height > 0 ? v.Height : FallbackHeight;
        return new Point(v.X + w / 2, v.Y + h / 2);
    }
}
