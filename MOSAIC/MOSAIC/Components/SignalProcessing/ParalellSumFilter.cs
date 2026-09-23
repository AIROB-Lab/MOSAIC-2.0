namespace MOSAIC.Components.SignalProcessing;

/// <summary>
/// Runs two <see cref="IOnlineFilter"/> instances in parallel and sums their outputs.
/// </summary>
/// <remarks>
/// <para>
/// Used to implement a bandstop filter as <c>y[n] = LP(x[n]) + HP(x[n])</c>,
/// where the lowpass passes everything below the stop-band and the highpass passes
/// everything above it. This is the correct topology for a notch/bandstop response —
/// cascading (series) would produce an all-reject filter instead.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Not thread-safe. Callers must synchronize externally.
/// </para>
/// </remarks>
public class ParallelSumFilter : IOnlineFilter
{
    private readonly IOnlineFilter _filterA;
    private readonly IOnlineFilter _filterB;

    /// <summary>
    /// Initializes a new <see cref="ParallelSumFilter"/>.
    /// </summary>
    /// <param name="filterA">First parallel branch (e.g. lowpass).</param>
    /// <param name="filterB">Second parallel branch (e.g. highpass).</param>
    public ParallelSumFilter(IOnlineFilter filterA, IOnlineFilter filterB)
    {
        _filterA = filterA;
        _filterB = filterB;
    }

    /// <inheritdoc />
    public double ProcessSample(double sample)
    {
        return _filterA.ProcessSample(sample) + _filterB.ProcessSample(sample);
    }

    /// <inheritdoc />
    public double[] ProcessSamples(double[] samples)
    {
        var outA = _filterA.ProcessSamples(samples);
        var outB = _filterB.ProcessSamples(samples);
        var output = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            output[i] = outA[i] + outB[i];
        return output;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _filterA.Reset();
        _filterB.Reset();
    }
}