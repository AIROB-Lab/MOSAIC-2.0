using System;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace MOSAIC.Controls;

/// <summary>
/// A <see cref="NumericUpDown"/> that avoids the Avalonia Win32 UI-Automation crash where spinning
/// the value throws <c>ArgumentException: UnsupportedType (Parameter 'Decimal')</c> from
/// <c>ComVariant.Create</c> whenever a UI-Automation client is active (Narrator, an inspector tool,
/// etc.) — AvaloniaUI/Avalonia#17827.
///
/// The default <see cref="NumericUpDownAutomationPeer"/> raises a RangeValue property-changed event
/// carrying the control's <see cref="decimal"/> value, which the Win32 UIA marshaller can't convert.
/// We substitute a plain <see cref="ControlAutomationPeer"/>, which never raises that event, so the
/// spinner no longer crashes. (Accessibility clients lose the numeric value semantics for these
/// fields — an acceptable trade until the framework fix is picked up.)
/// </summary>
public class SafeNumericUpDown : NumericUpDown
{
    // A subclass otherwise resolves a control theme keyed to its own type (none exists) and renders
    // with no template. Point the style key at NumericUpDown so it picks up the built-in theme.
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    protected override AutomationPeer OnCreateAutomationPeer() => new ControlAutomationPeer(this);
}
