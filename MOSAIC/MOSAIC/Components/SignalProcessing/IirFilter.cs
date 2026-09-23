using System;

namespace MOSAIC.Components.SignalProcessing;

/// <summary>
/// IIR (Infinite Impulse Response) filter using the Direct Form II Transposed structure.
/// </summary>
/// <remarks>
/// <para>
/// The filter implements the difference equation:
/// <c>y[n] = (b[0]·x[n] + b[1]·x[n−1] + … ) − (a[1]·y[n−1] + a[2]·y[n−2] + … )</c>,
/// where <c>b</c> are the feedforward (numerator) coefficients and <c>a</c> are the feedback
/// (denominator) coefficients.
/// </para>
/// <para>
/// The Direct Form II Transposed structure is used because it minimises the number of delay
/// elements and offers better numerical properties than Direct Form I for high-order filters.
/// </para>
/// <para>
/// If <c>a[0] ≠ 1</c>, both coefficient arrays are automatically normalised during construction.
/// </para>
/// <para>
/// Coefficients are typically produced by <see cref="FilterDesign"/> factory methods.
/// </para>
/// <para>
/// <b>Thread-safety:</b> This type is not thread-safe. If concurrent access is required, the caller
/// must synchronize externally.
/// </para>
/// </remarks>
public class IirFilter : IOnlineFilter
{
    /// <summary>
    /// Feedforward (numerator) coefficients. Normalised so that <c>a[0] == 1</c>.
    /// </summary>
    private readonly double[] _b;

    /// <summary>
    /// Feedback (denominator) coefficients. Normalised so that <c>a[0] == 1</c>.
    /// </summary>
    private readonly double[] _a;

    /// <summary>
    /// Internal state (delay) variables for the Direct Form II Transposed structure.
    /// Length equals <c>max(b.Length, a.Length)</c>.
    /// </summary>
    private readonly double[] _z;

    /// <summary>
    /// Gets the filter order, defined as <c>max(b.Length, a.Length) − 1</c>.
    /// </summary>
    public int Order => Math.Max(_b.Length, _a.Length) - 1;

    /// <summary>
    /// Initializes a new <see cref="IirFilter"/> with the given transfer-function coefficients.
    /// </summary>
    /// <param name="b">
    /// Feedforward (numerator) coefficients. The array is stored by reference (not copied)
    /// and may be modified in-place if normalisation is required.
    /// </param>
    /// <param name="a">
    /// Feedback (denominator) coefficients. <c>a[0]</c> is expected to be 1; if not, both
    /// <paramref name="b"/> and <paramref name="a"/> are normalised by dividing through by <c>a[0]</c>.
    /// The array is stored by reference and may be modified in-place.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="b"/> or <paramref name="a"/> is <see langword="null"/>.
    /// </exception>
    public IirFilter(double[] b, double[] a)
    {
        _b = b ?? throw new ArgumentNullException(nameof(b));
        _a = a ?? throw new ArgumentNullException(nameof(a));

        if (Math.Abs(_a[0] - 1.0) > 1e-10)
        {
            var a0 = _a[0];
            for (int i = 0; i < _b.Length; i++) _b[i] /= a0;
            for (int i = 0; i < _a.Length; i++) _a[i] /= a0;
        }

        _z = new double[Math.Max(_b.Length, _a.Length)];
    }

    /// <summary>
    /// Processes a single input sample through the filter and returns the filtered output.
    /// </summary>
    /// <param name="sample">The input sample value.</param>
    /// <returns>The filtered output sample.</returns>
    /// <remarks>
    /// Uses the Direct Form II Transposed update equations:
    /// <code>
    /// y[n]  = b[0]·x[n] + z[0]
    /// z[i]  = b[i+1]·x[n] − a[i+1]·y[n] + z[i+1]   (for i = 0 … N−2)
    /// </code>
    /// </remarks>
    public double ProcessSample(double sample)
    {
        double output = _b[0] * sample + _z[0];

        for (int i = 1; i < _z.Length; i++)
        {
            _z[i - 1] = _b.Length > i ? _b[i] * sample : 0;
            _z[i - 1] += _z[i];
            _z[i - 1] -= _a.Length > i ? _a[i] * output : 0;
        }

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
    /// Resets the filter state by clearing all delay elements, as if no samples had been processed.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_z, 0, _z.Length);
    }
}