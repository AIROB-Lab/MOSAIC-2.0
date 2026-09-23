namespace MOSAIC.Components.SignalProcessing;

/// <summary>
/// Interface for online (sample-by-sample) digital filters that maintain internal state across calls.
/// </summary>
/// <remarks>
/// <para>
/// Implementations (e.g., <see cref="FirFilter"/>, <see cref="IirFilter"/>) process one sample at a time
/// via <see cref="ProcessSample"/> while maintaining an internal delay line or state vector between calls.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Implementations are generally not thread-safe. Callers must synchronize
/// externally if concurrent access is required.
/// </para>
/// </remarks>
public interface IOnlineFilter
{
    /// <summary>
    /// Processes a single input sample and returns the filtered output.
    /// </summary>
    /// <param name="sample">The input sample value.</param>
    /// <returns>The filtered output sample.</returns>
    double ProcessSample(double sample);

    /// <summary>
    /// Processes an array of input samples sequentially and returns the filtered outputs.
    /// </summary>
    /// <param name="samples">The input sample array.</param>
    /// <returns>A new array of the same length containing the filtered output samples.</returns>
    double[] ProcessSamples(double[] samples);

    /// <summary>
    /// Resets all internal filter state (delay lines, history buffers), as if no samples had been processed.
    /// </summary>
    void Reset();
}