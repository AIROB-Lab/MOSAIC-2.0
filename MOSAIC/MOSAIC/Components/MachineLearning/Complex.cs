using System;

namespace MOSAIC.Components.MachineLearning
{
    /// <summary>
    /// Simple complex number for mathematical computations.
    /// </summary>
    public readonly struct Complex
    {
        public readonly double Real;
        public readonly double Imag;

        public Complex(double real, double imag = 0)
        {
            Real = real;
            Imag = imag;
        }

        public static Complex operator +(Complex a, Complex b) => new(a.Real + b.Real, a.Imag + b.Imag);
        public static Complex operator -(Complex a, Complex b) => new(a.Real - b.Real, a.Imag - b.Imag);
        public static Complex operator *(Complex a, Complex b) => 
            new(a.Real * b.Real - a.Imag * b.Imag, a.Real * b.Imag + a.Imag * b.Real);
        public static Complex operator *(Complex a, double b) => new(a.Real * b, a.Imag * b);
        public static Complex operator *(double a, Complex b) => new(a * b.Real, a * b.Imag);
        
        public static Complex operator /(Complex a, Complex b)
        {
            var denom = b.Real * b.Real + b.Imag * b.Imag;
            return new Complex(
                (a.Real * b.Real + a.Imag * b.Imag) / denom,
                (a.Imag * b.Real - a.Real * b.Imag) / denom);
        }
        
        public static Complex operator /(double a, Complex b)
        {
            var denom = b.Real * b.Real + b.Imag * b.Imag;
            return new Complex(a * b.Real / denom, -a * b.Imag / denom);
        }
        
        public static Complex operator /(Complex a, double b) => new(a.Real / b, a.Imag / b);

        public double Magnitude => Math.Sqrt(Real * Real + Imag * Imag);
        public double Phase => Math.Atan2(Imag, Real);
        public Complex Conjugate => new(Real, -Imag);

        public static Complex FromPolar(double magnitude, double phase) =>
            new(magnitude * Math.Cos(phase), magnitude * Math.Sin(phase));

        public static Complex Exp(Complex z)
        {
            var r = Math.Exp(z.Real);
            return new Complex(r * Math.Cos(z.Imag), r * Math.Sin(z.Imag));
        }

        public override string ToString() => Imag >= 0 ? $"{Real} + {Imag}i" : $"{Real} - {-Imag}i";
    }
}