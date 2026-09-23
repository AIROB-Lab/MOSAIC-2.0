namespace MOSAIC.Components.SignalProcessing;

/// <summary>
/// A no-op filter that returns input samples unmodified.
/// </summary>
/// <remarks>
/// <para>
/// Useful as a default or placeholder when a filter slot must be populated but no actual
/// filtering is desired. All methods are zero-cost pass-throughs with no internal state.
/// </para>
/// <para>
/// Unlike <see cref="FirFilter"/> and <see cref="IirFilter"/>, this implementation is inherently
/// thread-safe since it holds no mutable state.
/// </para>
/// </remarks>
public class PassthroughFilter : IOnlineFilter
{
    /// <inheritdoc />
    public double ProcessSample(double sample) => sample;

    /// <inheritdoc />
    /// <remarks>
    /// Returns the original array instance (no copy is made).
    /// </remarks>
    public double[] ProcessSamples(double[] samples) => samples;

    /// <inheritdoc />
    /// <remarks>No-op — there is no internal state to clear.</remarks>
    public void Reset() { }
}