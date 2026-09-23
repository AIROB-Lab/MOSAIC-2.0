using System;
using System.Linq;
using System.Numerics;

namespace MOSAIC.Components.SignalProcessing;

/// <summary>
/// Factory for creating standard FIR and IIR filter designs.
/// </summary>
/// <remarks>
/// <para>
/// This class provides static factory methods that compute filter coefficients and return
/// ready-to-use <see cref="FirFilter"/> or <see cref="IirFilter"/> instances.
/// </para>
/// <para>
/// <b>FIR filters</b> are designed using the windowed-sinc method with a Hamming window.
/// Highpass, bandpass, and bandstop variants are derived from the lowpass prototype via
/// spectral inversion and subtraction.
/// </para>
/// <para>
/// <b>IIR filters</b> use a Butterworth analog prototype transformed to the digital domain
/// via the bilinear transform. Bandpass and bandstop variants use the proper analog prototype
/// frequency transformations (LP→BP and LP→BS) to produce correct responses, including
/// narrow-band notch filters.
/// </para>
/// <para>
/// <b>Frequency convention:</b> All cutoff frequencies are specified in Hz (not normalized).
/// The sample rate must be provided so the design methods can compute normalized frequencies internally.
/// </para>
/// </remarks>
public static class FilterDesign
{
    /// <summary>
    /// Creates a lowpass FIR filter using the windowed-sinc method with a Hamming window.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="cutoffFreq">Cutoff frequency in Hz. Must be less than <c>sampleRate / 2</c>.</param>
    /// <param name="numTaps">Number of filter coefficients (filter order + 1). Higher values yield sharper roll-off.</param>
    /// <returns>A new <see cref="FirFilter"/> configured with the computed coefficients.</returns>
    public static FirFilter CreateFirLowpass(double sampleRate, double cutoffFreq, int numTaps)
    {
        var coefficients = DesignFirLowpass(sampleRate, cutoffFreq, numTaps);
        return new FirFilter(coefficients);
    }

    /// <summary>
    /// Creates a highpass FIR filter using spectral inversion of a lowpass prototype.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="cutoffFreq">Cutoff frequency in Hz. Must be less than <c>sampleRate / 2</c>.</param>
    /// <param name="numTaps">Number of filter coefficients. Should be odd for a symmetric highpass response.</param>
    /// <returns>A new <see cref="FirFilter"/> configured with the computed coefficients.</returns>
    public static FirFilter CreateFirHighpass(double sampleRate, double cutoffFreq, int numTaps)
    {
        var coefficients = DesignFirHighpass(sampleRate, cutoffFreq, numTaps);
        return new FirFilter(coefficients);
    }

    /// <summary>
    /// Creates a bandpass FIR filter by subtracting two lowpass prototypes.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="lowCutoff">Lower cutoff frequency in Hz.</param>
    /// <param name="highCutoff">Upper cutoff frequency in Hz. Must be greater than <paramref name="lowCutoff"/>.</param>
    /// <param name="numTaps">Number of filter coefficients.</param>
    /// <returns>A new <see cref="FirFilter"/> configured with the computed coefficients.</returns>
    public static FirFilter CreateFirBandpass(double sampleRate, double lowCutoff, double highCutoff, int numTaps)
    {
        var coefficients = DesignFirBandpass(sampleRate, lowCutoff, highCutoff, numTaps);
        return new FirFilter(coefficients);
    }

    /// <summary>
    /// Creates a bandstop (notch) FIR filter via spectral inversion of a bandpass prototype.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="lowCutoff">Lower cutoff frequency in Hz.</param>
    /// <param name="highCutoff">Upper cutoff frequency in Hz. Must be greater than <paramref name="lowCutoff"/>.</param>
    /// <param name="numTaps">Number of filter coefficients.</param>
    /// <returns>A new <see cref="FirFilter"/> configured with the computed coefficients.</returns>
    public static FirFilter CreateFirBandstop(double sampleRate, double lowCutoff, double highCutoff, int numTaps)
    {
        var coefficients = DesignFirBandstop(sampleRate, lowCutoff, highCutoff, numTaps);
        return new FirFilter(coefficients);
    }

    /// <summary>
    /// Creates a lowpass IIR Butterworth filter.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="cutoffFreq">Cutoff frequency in Hz. Must be less than <c>sampleRate / 2</c>.</param>
    /// <param name="order">Filter order. Higher values yield sharper roll-off but may introduce numerical instability.</param>
    /// <returns>A new <see cref="IirFilter"/> with the computed numerator (<c>b</c>) and denominator (<c>a</c>) coefficients.</returns>
    public static IirFilter CreateIirLowpass(double sampleRate, double cutoffFreq, int order)
    {
        var (b, a) = DesignButterworthLowpass(sampleRate, cutoffFreq, order);
        return new IirFilter(b, a);
    }

    /// <summary>
    /// Creates a highpass IIR Butterworth filter.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="cutoffFreq">Cutoff frequency in Hz. Must be less than <c>sampleRate / 2</c>.</param>
    /// <param name="order">Filter order.</param>
    /// <returns>A new <see cref="IirFilter"/> with the computed numerator (<c>b</c>) and denominator (<c>a</c>) coefficients.</returns>
    public static IirFilter CreateIirHighpass(double sampleRate, double cutoffFreq, int order)
    {
        var (b, a) = DesignButterworthHighpass(sampleRate, cutoffFreq, order);
        return new IirFilter(b, a);
    }

    /// <summary>
    /// Creates a bandpass IIR Butterworth filter using the proper analog prototype transformation.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="lowCutoff">Lower cutoff frequency in Hz.</param>
    /// <param name="highCutoff">Upper cutoff frequency in Hz. Must be greater than <paramref name="lowCutoff"/>.</param>
    /// <param name="order">Filter order of the prototype. The resulting filter has order <c>2 × order</c>.</param>
    /// <returns>A new <see cref="IirFilter"/> with the computed numerator (<c>b</c>) and denominator (<c>a</c>) coefficients.</returns>
    /// <remarks>
    /// Uses the standard LP→BP analog frequency transformation <c>s → (s² + ω₀²) / (BW·s)</c>
    /// where <c>ω₀ = √(ωL·ωH)</c> and <c>BW = ωH − ωL</c>. Each prototype pole maps to two
    /// bandpass poles, and N zeros are placed at DC (<c>z = 1</c>) and Nyquist (<c>z = −1</c>).
    /// </remarks>
    public static IirFilter CreateIirBandpass(double sampleRate, double lowCutoff, double highCutoff, int order)
    {
        var (b, a) = DesignButterworthBandpass(sampleRate, lowCutoff, highCutoff, order);
        return new IirFilter(b, a);
    }

    /// <summary>
    /// Creates a bandstop (notch) IIR Butterworth filter using the proper analog prototype transformation.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="lowCutoff">Lower cutoff frequency in Hz.</param>
    /// <param name="highCutoff">Upper cutoff frequency in Hz. Must be greater than <paramref name="lowCutoff"/>.</param>
    /// <param name="order">Filter order of the prototype. The resulting filter has order <c>2 × order</c>.</param>
    /// <returns>A new <see cref="IirFilter"/> with the computed numerator (<c>b</c>) and denominator (<c>a</c>) coefficients.</returns>
    /// <remarks>
    /// Uses the standard LP→BS analog frequency transformation <c>s → (BW·s) / (s² + ω₀²)</c>.
    /// Each prototype pole maps to two bandstop poles, and 2N zeros are placed at <c>±jω₀</c>
    /// (which map to conjugate pairs on the unit circle at the notch frequency after bilinear transform).
    /// </remarks>
    public static IirFilter CreateIirBandstop(double sampleRate, double lowCutoff, double highCutoff, int order)
    {
        var (b, a) = DesignButterworthBandstop(sampleRate, lowCutoff, highCutoff, order);
        return new IirFilter(b, a);
    }

    #region FIR Design Methods

    /// <summary>
    /// Designs a lowpass FIR filter using the windowed-sinc method with a Hamming window.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="cutoffFreq">Cutoff frequency in Hz.</param>
    /// <param name="numTaps">Number of filter coefficients.</param>
    /// <returns>Normalized filter coefficients whose sum equals 1 (unity DC gain).</returns>
    private static double[] DesignFirLowpass(double sampleRate, double cutoffFreq, int numTaps)
    {
        var fc = cutoffFreq / sampleRate;
        var coefficients = new double[numTaps];
        var middle = (numTaps - 1) / 2.0;

        for (int i = 0; i < numTaps; i++)
        {
            var n = i - middle;
            if (Math.Abs(n) < 1e-10)
            {
                coefficients[i] = 2 * fc;
            }
            else
            {
                coefficients[i] = Math.Sin(2 * Math.PI * fc * n) / (Math.PI * n);
            }

            // Apply Hamming window
            coefficients[i] *= 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (numTaps - 1));
        }

        // Normalize for unity DC gain
        var sum = coefficients.Sum();
        for (int i = 0; i < numTaps; i++)
            coefficients[i] /= sum;

        return coefficients;
    }

    /// <summary>
    /// Designs a highpass FIR filter by applying spectral inversion to a lowpass prototype.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="cutoffFreq">Cutoff frequency in Hz.</param>
    /// <param name="numTaps">Number of filter coefficients.</param>
    /// <returns>Highpass filter coefficients.</returns>
    private static double[] DesignFirHighpass(double sampleRate, double cutoffFreq, int numTaps)
    {
        var lowpass = DesignFirLowpass(sampleRate, cutoffFreq, numTaps);
        var highpass = new double[numTaps];
        var middle = (numTaps - 1) / 2;

        for (int i = 0; i < numTaps; i++)
        {
            highpass[i] = -lowpass[i];
        }

        highpass[middle] += 1.0;

        return highpass;
    }

    /// <summary>
    /// Designs a bandpass FIR filter by subtracting a low-cutoff lowpass from a high-cutoff lowpass.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="lowCutoff">Lower cutoff frequency in Hz.</param>
    /// <param name="highCutoff">Upper cutoff frequency in Hz.</param>
    /// <param name="numTaps">Number of filter coefficients.</param>
    /// <returns>Bandpass filter coefficients.</returns>
    private static double[] DesignFirBandpass(double sampleRate, double lowCutoff, double highCutoff, int numTaps)
    {
        var lowpass1 = DesignFirLowpass(sampleRate, highCutoff, numTaps);
        var lowpass2 = DesignFirLowpass(sampleRate, lowCutoff, numTaps);
        var bandpass = new double[numTaps];

        for (int i = 0; i < numTaps; i++)
        {
            bandpass[i] = lowpass1[i] - lowpass2[i];
        }

        return bandpass;
    }

    /// <summary>
    /// Designs a bandstop FIR filter by applying spectral inversion to a bandpass prototype.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="lowCutoff">Lower cutoff frequency in Hz.</param>
    /// <param name="highCutoff">Upper cutoff frequency in Hz.</param>
    /// <param name="numTaps">Number of filter coefficients.</param>
    /// <returns>Bandstop filter coefficients.</returns>
    private static double[] DesignFirBandstop(double sampleRate, double lowCutoff, double highCutoff, int numTaps)
    {
        var bandpass = DesignFirBandpass(sampleRate, lowCutoff, highCutoff, numTaps);
        var bandstop = new double[numTaps];
        var middle = (numTaps - 1) / 2;

        for (int i = 0; i < numTaps; i++)
        {
            bandstop[i] = -bandpass[i];
        }

        bandstop[middle] += 1.0;

        return bandstop;
    }

    #endregion

    #region IIR Butterworth Design Methods

    /// <summary>
    /// Returns the Butterworth lowpass analog prototype poles on the unit circle (left half-plane).
    /// </summary>
    /// <param name="order">Filter order.</param>
    /// <returns>Array of <paramref name="order"/> poles with negative real parts.</returns>
    private static Complex[] ButterworthPrototypePoles(int order)
    {
        var poles = new Complex[order];
        for (int k = 0; k < order; k++)
        {
            var theta = Math.PI * (2 * k + 1) / (2 * order) + Math.PI / 2;
            poles[k] = new Complex(Math.Cos(theta), Math.Sin(theta));
        }
        return poles;
    }

    /// <summary>
    /// Designs a lowpass Butterworth IIR filter using analog prototype poles and the bilinear transform.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="cutoffFreq">Cutoff frequency in Hz.</param>
    /// <param name="order">Filter order.</param>
    /// <returns>
    /// A tuple of numerator (<c>b</c>) and denominator (<c>a</c>) polynomial coefficients
    /// in descending powers of <c>z</c>.
    /// </returns>
    private static (double[] b, double[] a) DesignButterworthLowpass(double sampleRate, double cutoffFreq, int order)
    {
        var wc = Math.Tan(Math.PI * cutoffFreq / sampleRate);

        var poles = new Complex[order];
        for (int k = 0; k < order; k++)
        {
            var theta = Math.PI * (2 * k + 1) / (2 * order) + Math.PI / 2;
            poles[k] = new Complex(Math.Cos(theta), Math.Sin(theta)) * wc;
        }

        return BilinearTransform(new Complex[0], poles, wc);
    }

    /// <summary>
    /// Designs a highpass Butterworth IIR filter using the <c>s → ωc/s</c> frequency transformation.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="cutoffFreq">Cutoff frequency in Hz.</param>
    /// <param name="order">Filter order.</param>
    /// <returns>
    /// A tuple of numerator (<c>b</c>) and denominator (<c>a</c>) polynomial coefficients
    /// in descending powers of <c>z</c>.
    /// </returns>
    private static (double[] b, double[] a) DesignButterworthHighpass(double sampleRate, double cutoffFreq, int order)
    {
        var wc = Math.Tan(Math.PI * cutoffFreq / sampleRate);

        var poles = new Complex[order];
        var zeros = new Complex[order];

        for (int k = 0; k < order; k++)
        {
            var theta = Math.PI * (2 * k + 1) / (2 * order) + Math.PI / 2;
            var analogPole = new Complex(Math.Cos(theta), Math.Sin(theta));
            poles[k] = wc / analogPole;
            zeros[k] = new Complex(0, 0);
        }

        return BilinearTransform(zeros, poles, wc, isHighpass: true);
    }

    /// <summary>
    /// Designs a bandpass Butterworth IIR filter using the proper analog prototype transformation.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="lowCutoff">Lower cutoff frequency in Hz.</param>
    /// <param name="highCutoff">Upper cutoff frequency in Hz.</param>
    /// <param name="order">Prototype filter order. The resulting filter has order 2N.</param>
    /// <returns>
    /// A tuple of numerator (<c>b</c>) and denominator (<c>a</c>) polynomial coefficients.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The LP→BP transformation maps each prototype pole <c>p</c> to two analog poles:
    /// <c>p_bp = (p·BW/2) ± √((p·BW/2)² − ω₀²)</c>
    /// </para>
    /// <para>
    /// Zeros: N at <c>s = 0</c> (map to <c>z = 1</c>) and N at <c>s = ∞</c> (map to <c>z = −1</c>).
    /// </para>
    /// </remarks>
    private static (double[] b, double[] a) DesignButterworthBandpass(double sampleRate, double lowCutoff,
        double highCutoff, int order)
    {
        // Pre-warp cutoff frequencies
        double wL = 2 * sampleRate * Math.Tan(Math.PI * lowCutoff / sampleRate);
        double wH = 2 * sampleRate * Math.Tan(Math.PI * highCutoff / sampleRate);
        double w0 = Math.Sqrt(wL * wH);
        double BW = wH - wL;

        var protoPoles = ButterworthPrototypePoles(order);

        // LP → BP: each prototype pole maps to two analog bandpass poles
        var analogPoles = new Complex[2 * order];
        for (int k = 0; k < order; k++)
        {
            var half = protoPoles[k] * BW / 2;
            var disc = half * half - w0 * w0;
            var sq = Complex.Sqrt(disc);
            analogPoles[2 * k] = half + sq;
            analogPoles[2 * k + 1] = half - sq;
        }

        // Zeros: N at s=0 → z=1, N at s=∞ → z=-1
        var digitalPoles = new Complex[2 * order];
        var digitalZeros = new Complex[2 * order];
        for (int i = 0; i < 2 * order; i++)
        {
            var s = analogPoles[i] / (2 * sampleRate);
            digitalPoles[i] = (Complex.One + s) / (Complex.One - s);
        }
        for (int i = 0; i < order; i++)
        {
            digitalZeros[i] = Complex.One;           // from s = 0
            digitalZeros[order + i] = -Complex.One;   // from s = ∞
        }

        var a = PolyFromRoots(digitalPoles);
        var b = PolyFromRoots(digitalZeros);

        // Normalize for unity gain at center frequency
        double centerHz = (lowCutoff + highCutoff) / 2;
        double wCenter = 2 * Math.PI * centerHz / sampleRate;
        var zCenter = new Complex(Math.Cos(wCenter), Math.Sin(wCenter));

        Complex Ha = Complex.Zero, Hb = Complex.Zero;
        for (int i = 0; i < a.Length; i++)
        {
            var zPow = Complex.Pow(zCenter, -(a.Length - 1 - i));
            Ha += a[i] * zPow;
            if (i < b.Length) Hb += b[i] * zPow;
        }
        double gain = (Ha / Hb).Magnitude;
        for (int i = 0; i < b.Length; i++) b[i] *= gain;

        return (b, a);
    }

    /// <summary>
    /// Designs a bandstop (notch) Butterworth IIR filter using the proper analog prototype transformation.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="lowCutoff">Lower cutoff frequency in Hz.</param>
    /// <param name="highCutoff">Upper cutoff frequency in Hz.</param>
    /// <param name="order">Prototype filter order. The resulting filter has order 2N.</param>
    /// <returns>
    /// A tuple of numerator (<c>b</c>) and denominator (<c>a</c>) polynomial coefficients.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The LP→BS transformation maps each prototype pole <c>p</c> to two analog poles:
    /// <c>p_bs = BW/(2p) ± √((BW/(2p))² − ω₀²)</c>
    /// </para>
    /// <para>
    /// Zeros: 2N zeros at <c>s = ±jω₀</c>, which after bilinear transform become conjugate pairs
    /// on the unit circle at the notch frequency, producing deep rejection at the center.
    /// </para>
    /// </remarks>
    private static (double[] b, double[] a) DesignButterworthBandstop(double sampleRate, double lowCutoff,
        double highCutoff, int order)
    {
        // Pre-warp cutoff frequencies
        double wL = 2 * sampleRate * Math.Tan(Math.PI * lowCutoff / sampleRate);
        double wH = 2 * sampleRate * Math.Tan(Math.PI * highCutoff / sampleRate);
        double w0 = Math.Sqrt(wL * wH);
        double BW = wH - wL;

        var protoPoles = ButterworthPrototypePoles(order);

        // LP → BS: each prototype pole p maps to two analog bandstop poles
        // s = BW/(2p) ± sqrt((BW/(2p))² − ω₀²)
        var analogPoles = new Complex[2 * order];
        for (int k = 0; k < order; k++)
        {
            var half = BW / (2 * protoPoles[k]);
            var disc = half * half - w0 * w0;
            var sq = Complex.Sqrt(disc);
            analogPoles[2 * k] = half + sq;
            analogPoles[2 * k + 1] = half - sq;
        }

        // Zeros: N pairs at ±jω₀ in the analog domain
        var analogZeros = new Complex[2 * order];
        for (int k = 0; k < order; k++)
        {
            analogZeros[2 * k] = new Complex(0, w0);
            analogZeros[2 * k + 1] = new Complex(0, -w0);
        }

        // Bilinear transform: z = (1 + s/(2fs)) / (1 - s/(2fs))
        var digitalPoles = new Complex[2 * order];
        var digitalZeros = new Complex[2 * order];
        for (int i = 0; i < 2 * order; i++)
        {
            var sp = analogPoles[i] / (2 * sampleRate);
            digitalPoles[i] = (Complex.One + sp) / (Complex.One - sp);

            var sz = analogZeros[i] / (2 * sampleRate);
            digitalZeros[i] = (Complex.One + sz) / (Complex.One - sz);
        }

        var a = PolyFromRoots(digitalPoles);
        var b = PolyFromRoots(digitalZeros);

        // Normalize for unity gain at DC (z = 1)
        double sumA = a.Sum();
        double sumB = b.Sum();
        double gain = sumA / sumB;
        for (int i = 0; i < b.Length; i++) b[i] *= gain;

        return (b, a);
    }

    /// <summary>
    /// Applies the bilinear transform to convert analog poles and zeros into digital filter coefficients.
    /// </summary>
    /// <param name="zeros">Analog-domain zeros.</param>
    /// <param name="poles">Analog-domain poles.</param>
    /// <param name="wc">Pre-warped cutoff frequency.</param>
    /// <param name="isHighpass">
    /// When <see langword="true"/>, zeros are placed at <c>z = 1</c> and gain is normalized at Nyquist.
    /// When <see langword="false"/> (default), zeros are placed at <c>z = −1</c> and gain is normalized at DC.
    /// </param>
    /// <returns>
    /// A tuple of numerator (<c>b</c>) and denominator (<c>a</c>) polynomial coefficients
    /// normalized for unity gain at DC (lowpass) or Nyquist (highpass).
    /// </returns>
    private static (double[] b, double[] a) BilinearTransform(Complex[] zeros, Complex[] poles, double wc,
        bool isHighpass = false)
    {
        int order = poles.Length;

        var digitalPoles = new Complex[order];
        var digitalZeros = new Complex[order];

        for (int i = 0; i < order; i++)
        {
            var pa = poles[i];
            digitalPoles[i] = (new Complex(1, 0) + pa) / (new Complex(1, 0) - pa);

            if (isHighpass || i < zeros.Length)
            {
                digitalZeros[i] = isHighpass
                    ? new Complex(1, 0)
                    : (new Complex(1, 0) + zeros[i]) / (new Complex(1, 0) - zeros[i]);
            }
        }

        var a = PolyFromRoots(digitalPoles);
        var b = isHighpass ? PolyFromRoots(digitalZeros) : new double[order + 1];

        if (!isHighpass)
        {
            b = PolyFromRoots(Enumerable.Repeat(new Complex(-1, 0), order).ToArray());
        }

        // Normalize for unity gain at DC (lowpass) or Nyquist (highpass)
        double sumB = 0, sumA = 0;
        if (isHighpass)
        {
            for (int i = 0; i < b.Length; i++) sumB += b[i] * (i % 2 == 0 ? 1 : -1);
            for (int i = 0; i < a.Length; i++) sumA += a[i] * (i % 2 == 0 ? 1 : -1);
        }
        else
        {
            sumB = b.Sum();
            sumA = a.Sum();
        }

        var gain = sumA / sumB;
        for (int i = 0; i < b.Length; i++) b[i] *= gain;

        return (b, a);
    }

    /// <summary>
    /// Expands a set of complex roots into real polynomial coefficients.
    /// </summary>
    /// <param name="roots">The roots (zeros or poles) of the polynomial.</param>
    /// <returns>
    /// Real-valued polynomial coefficients in descending powers.
    /// Imaginary parts are discarded (assumed negligible for conjugate-pair roots).
    /// </returns>
    private static double[] PolyFromRoots(Complex[] roots)
    {
        var coeffs = new Complex[roots.Length + 1];
        coeffs[0] = new Complex(1, 0);

        for (int i = 0; i < roots.Length; i++)
        {
            for (int j = i + 1; j > 0; j--)
            {
                coeffs[j] = coeffs[j] - roots[i] * coeffs[j - 1];
            }
        }

        return coeffs.Select(c => c.Real).ToArray();
    }

    /// <summary>
    /// Computes the discrete convolution of two coefficient arrays.
    /// </summary>
    /// <param name="a">First coefficient array.</param>
    /// <param name="b">Second coefficient array.</param>
    /// <returns>
    /// The convolution result with length <c>a.Length + b.Length − 1</c>.
    /// </returns>
    /// <remarks>
    /// Cascades two transfer functions by convolving their numerator and denominator polynomials.
    /// Currently unused: the bandpass and bandstop designs build their polynomials directly from
    /// the transformed poles and zeros via <see cref="PolyFromRoots"/> rather than by cascading.
    /// </remarks>
    private static double[] Convolve(double[] a, double[] b)
    {
        var result = new double[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
        {
            for (int j = 0; j < b.Length; j++)
            {
                result[i + j] += a[i] * b[j];
            }
        }

        return result;
    }

    #endregion
}