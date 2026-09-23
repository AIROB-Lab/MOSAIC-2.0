using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MOSAIC.ViewModels.Devices;

/// <summary>
/// One row of the BodyRig card's kinematic-chain strip: a segment shown at its depth in the
/// tree, with the sensor bound to it and whether that sensor is currently delivering packets.
/// </summary>
/// <remarks>
/// <para>
/// The strip exists because the chain used to be invisible. Parenting was a bare numeric box, so
/// the two facts that actually matter — what hangs off what, and which IMU drives which segment —
/// could not be seen together, or at all.
/// </para>
/// <para>
/// Deliberately holds no brushes. Colours come from converters and style classes in the view, so
/// they follow a theme switch; an <c>IBrush</c> captured here would go stale.
/// </para>
/// </remarks>
public partial class ChainNodeViewModel : ObservableObject
{
    /// <summary>Indent per depth level, in device-independent pixels.</summary>
    private const double IndentPerLevel = 12;

    /// <summary>
    /// Indent ceiling. The card is ~440px wide and the row already spends ~140px on the glyph,
    /// index pill, sensor chip and liveness dot; letting a deep chain indent without bound would
    /// squeeze the segment name to nothing.
    /// </summary>
    private const double MaxIndent = 48;

    /// <summary>Index of this segment in the block's chain array.</summary>
    public int Index { get; }

    /// <summary>Depth in the tree; 0 for a root or an unparented segment.</summary>
    public int Depth { get; }

    /// <summary>Left indent for this row, derived from <see cref="Depth"/> and capped.</summary>
    public double IndentWidth => Math.Min(Depth * IndentPerLevel, MaxIndent);

    /// <summary>
    /// Leading glyph: a filled dot for a root, an elbow for a child, a warning sign for a
    /// segment that could not be reached from any root (a cycle, or a dangling parent).
    /// </summary>
    public string Glyph { get; }

    /// <summary>Segment name, falling back to its index when unnamed.</summary>
    [ObservableProperty] private string _displayName = string.Empty;

    /// <summary>Short sensor tag, e.g. <c>S3</c>, or an em dash when the segment is passive.</summary>
    [ObservableProperty] private string _sensorLabel = "—";

    /// <summary>Hover text explaining the sensor binding and its packet activity.</summary>
    [ObservableProperty] private string _sensorTooltip = string.Empty;

    /// <summary>Whether this row is the one the editors below the strip are bound to.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// Whether the bound sensor delivered a packet recently. False for a passive segment and
    /// for a bound sensor that has gone quiet.
    /// </summary>
    [ObservableProperty] private bool _isLive;

    /// <summary>Initialises a row for <paramref name="index"/> at <paramref name="depth"/>.</summary>
    public ChainNodeViewModel(int index, int depth, string glyph)
    {
        Index = index;
        Depth = depth;
        Glyph = glyph;
    }
}
