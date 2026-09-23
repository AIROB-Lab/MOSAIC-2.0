using System;

namespace MOSAIC.Components.SignalProcessing;

/// <summary>
/// FIR (Finite Impulse Response) filter that processes samples one at a time using a circular delay line.
/// </summary>
/// <remarks>
/// <para>
/// The filter computes the output as the discrete convolution of the input with the coefficient array:
/// <c>y[n] = Σ b[k] · x[n−k]</c>, where <c>b</c> are the coefficients and <c>x</c> is the input history.
/// </para>
/// <para>
/// A circular buffer (<see cref="_delayLine"/>) is used to avoid shifting the entire history on each sample,
/// giving O(N) per sample where N is the number of taps.
/// </para>
/// <para>
/// Coefficients are typically produced by <see cref="FilterDesign"/> factory methods.
/// </para>
/// <para>
/// <b>Thread-safety:</b> This type is not thread-safe. If concurrent access is required, the caller
/// must synchronize externally.
/// </para>
/// </remarks>
public class FirFilter : IOnlineFilter
{
    /// <summary>
    /// Filter coefficients (taps) applied during convolution.
    /// </summary>
    private readonly double[] _coefficients;

    /// <summary>
    /// Circular buffer holding the most recent input samples.
    /// </summary>
    private readonly double[] _delayLine;

    /// <summary>
    /// Current write position in the circular delay line.
    /// </summary>
    private int _delayIndex;

    /// <summary>
    /// Gets the filter order (number of coefficients / taps).
    /// </summary>
    public int Order => _coefficients.Length;

    /// <summary>
    /// Initializes a new <see cref="FirFilter"/> with the specified coefficients.
    /// </summary>
    /// <param name="coefficients">
    /// The filter tap weights. The array is stored by reference (not copied).
    /// Must contain at least one element.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="coefficients"/> is <see langword="null"/>.
    /// </exception>
    public FirFilter(double[] coefficients)
    {
        _coefficients = coefficients ?? throw new ArgumentNullException(nameof(coefficients));
        _delayLine = new double[coefficients.Length];
        _delayIndex = 0;
    }

    /// <summary>
    /// Processes a single input sample through the filter and returns the filtered output.
    /// </summary>
    /// <param name="sample">The input sample value.</param>
    /// <returns>The filtered output sample computed via convolution with the coefficient array.</returns>
    public double ProcessSample(double sample)
    {
        _delayLine[_delayIndex] = sample;

        double output = 0;
        int index = _delayIndex;
        for (int i = 0; i < _coefficients.Length; i++)
        {
            output += _coefficients[i] * _delayLine[index];
            index--;
            if (index < 0) index = _coefficients.Length - 1;
        }

        _delayIndex++;
        if (_delayIndex >= _coefficients.Length) _delayIndex = 0;

        return output;
    }

    /// <summary>
    /// Processes an array of input samples sequentially through the filter.
    /// </summary>
    /// <param name="samples">The input sample array.</param>
    /// <returns>A new array of the same length containing the filtered output samples.</returns>
    public double[] ProcessSamples(double[] samples)
    {
        var output = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            output[i] = ProcessSample(samples[i]);
        }
        return output;
    }

    /// <summary>
    /// Resets the filter state by clearing the delay line, as if no samples had been processed.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_delayLine, 0, _delayLine.Length);
        _delayIndex = 0;
    }
}