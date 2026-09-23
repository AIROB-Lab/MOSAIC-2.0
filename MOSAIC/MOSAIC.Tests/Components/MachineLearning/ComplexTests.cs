using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.MachineLearning;

namespace MOSAIC.Tests.Components.MachineLearning;

/// <summary>
/// Known-answer tests for <see cref="Complex"/>, a plain (real, imag) complex-number value type.
/// Every expected value is derived by hand from the standard complex-arithmetic definitions:
///   (a+bi) + (c+di) = (a+c) + (b+d)i
///   (a+bi) - (c+di) = (a-c) + (b-d)i
///   (a+bi)(c+di)    = (ac - bd) + (ad + bc)i
///   (a+bi)/(c+di)   = [(ac + bd) + (bc - ad)i] / (c² + d²)
///   |a+bi| = sqrt(a² + b²),  arg(a+bi) = atan2(b, a),  conj(a+bi) = a - bi
///   FromPolar(r, θ) = (r·cosθ, r·sinθ),  exp(a+bi) = e^a·(cos b + i·sin b)
/// </summary>
[TestClass]
public class ComplexTests
{
    [TestMethod]
    public void Constructor_RealAndImag_StoresBothParts()
    {
        // Fields hold exactly what was passed in.
        var z = new Complex(2.0, -3.0);

        Assert.AreEqual(2.0, z.Real, 1e-9);
        Assert.AreEqual(-3.0, z.Imag, 1e-9);
    }

    [TestMethod]
    public void Constructor_ImagDefaultsToZero_ForRealOnlyValue()
    {
        // The imag parameter defaults to 0, giving a purely-real value.
        var z = new Complex(4.5);

        Assert.AreEqual(4.5, z.Real, 1e-9);
        Assert.AreEqual(0.0, z.Imag, 1e-9);
    }

    [DataTestMethod]
    // (1+2i)+(3+4i) = (1+3) + (2+4)i = 4 + 6i
    [DataRow(1.0, 2.0, 3.0, 4.0, 4.0, 6.0)]
    // (-1-1i)+(2+5i) = 1 + 4i
    [DataRow(-1.0, -1.0, 2.0, 5.0, 1.0, 4.0)]
    public void OperatorAdd_TwoComplex_AddsComponentwise(
        double ar, double ai, double br, double bi, double er, double ei)
    {
        var result = new Complex(ar, ai) + new Complex(br, bi);

        Assert.AreEqual(er, result.Real, 1e-9);
        Assert.AreEqual(ei, result.Imag, 1e-9);
    }

    [DataTestMethod]
    // (5+3i)-(2+1i) = (5-2) + (3-1)i = 3 + 2i
    [DataRow(5.0, 3.0, 2.0, 1.0, 3.0, 2.0)]
    // (0+0i)-(4-7i) = -4 + 7i
    [DataRow(0.0, 0.0, 4.0, -7.0, -4.0, 7.0)]
    public void OperatorSubtract_TwoComplex_SubtractsComponentwise(
        double ar, double ai, double br, double bi, double er, double ei)
    {
        var result = new Complex(ar, ai) - new Complex(br, bi);

        Assert.AreEqual(er, result.Real, 1e-9);
        Assert.AreEqual(ei, result.Imag, 1e-9);
    }

    [DataTestMethod]
    // (2+3i)(4+5i) = (2·4 - 3·5) + (2·5 + 3·4)i = (8-15) + (10+12)i = -7 + 22i
    [DataRow(2.0, 3.0, 4.0, 5.0, -7.0, 22.0)]
    // i·i = (0+1i)(0+1i) = (0-1) + (0+0)i = -1 + 0i
    [DataRow(0.0, 1.0, 0.0, 1.0, -1.0, 0.0)]
    // (3+0i)(0+2i) = (0-0) + (6+0)i = 0 + 6i
    [DataRow(3.0, 0.0, 0.0, 2.0, 0.0, 6.0)]
    public void OperatorMultiply_TwoComplex_UsesComplexProductRule(
        double ar, double ai, double br, double bi, double er, double ei)
    {
        var result = new Complex(ar, ai) * new Complex(br, bi);

        Assert.AreEqual(er, result.Real, 1e-9);
        Assert.AreEqual(ei, result.Imag, 1e-9);
    }

    [TestMethod]
    public void OperatorMultiply_ComplexByScalar_ScalesBothParts()
    {
        // (2 - 3i)·4 = 8 - 12i
        var result = new Complex(2.0, -3.0) * 4.0;

        Assert.AreEqual(8.0, result.Real, 1e-9);
        Assert.AreEqual(-12.0, result.Imag, 1e-9);
    }

    [TestMethod]
    public void OperatorMultiply_ScalarByComplex_ScalesBothParts()
    {
        // 4·(2 - 3i) = 8 - 12i  (scalar on the left overload)
        var result = 4.0 * new Complex(2.0, -3.0);

        Assert.AreEqual(8.0, result.Real, 1e-9);
        Assert.AreEqual(-12.0, result.Imag, 1e-9);
    }

    [TestMethod]
    public void OperatorDivide_TwoComplex_UsesConjugateDivisionRule()
    {
        // (1+1i)/(1-1i): denom = 1² + (-1)² = 2.
        // Real = (1·1 + 1·(-1)) / 2 = (1 - 1)/2 = 0.
        // Imag = (1·1 - 1·(-1)) / 2 = (1 + 1)/2 = 1.  => 0 + 1i
        var result = new Complex(1.0, 1.0) / new Complex(1.0, -1.0);

        Assert.AreEqual(0.0, result.Real, 1e-9);
        Assert.AreEqual(1.0, result.Imag, 1e-9);
    }

    [TestMethod]
    public void OperatorDivide_TwoComplex_ProductThenDivideRoundTrips()
    {
        // ((-7+22i)) / (4+5i) should recover (2+3i), since (2+3i)(4+5i) = -7+22i.
        var result = new Complex(-7.0, 22.0) / new Complex(4.0, 5.0);

        Assert.AreEqual(2.0, result.Real, 1e-9);
        Assert.AreEqual(3.0, result.Imag, 1e-9);
    }

    [TestMethod]
    public void OperatorDivide_ScalarByComplex_MatchesReciprocalDefinition()
    {
        // 2 / (1 + 1i): denom = 1² + 1² = 2.
        // Real = 2·1 / 2 = 1.  Imag = -2·1 / 2 = -1.  => 1 - 1i
        var result = 2.0 / new Complex(1.0, 1.0);

        Assert.AreEqual(1.0, result.Real, 1e-9);
        Assert.AreEqual(-1.0, result.Imag, 1e-9);
    }

    [TestMethod]
    public void OperatorDivide_ComplexByScalar_DividesBothParts()
    {
        // (6 - 9i) / 3 = 2 - 3i
        var result = new Complex(6.0, -9.0) / 3.0;

        Assert.AreEqual(2.0, result.Real, 1e-9);
        Assert.AreEqual(-3.0, result.Imag, 1e-9);
    }

    [DataTestMethod]
    // |3+4i| = sqrt(9+16) = sqrt(25) = 5
    [DataRow(3.0, 4.0, 5.0)]
    // |-6+8i| = sqrt(36+64) = sqrt(100) = 10
    [DataRow(-6.0, 8.0, 10.0)]
    // |0+0i| = 0
    [DataRow(0.0, 0.0, 0.0)]
    public void Magnitude_KnownTriples_ReturnsEuclideanNorm(double real, double imag, double expected)
    {
        var z = new Complex(real, imag);

        // sqrt(real² + imag²) — transcendental, use 1e-6 delta.
        Assert.AreEqual(expected, z.Magnitude, 1e-6);
    }

    [DataTestMethod]
    // arg(1+0i) = atan2(0, 1) = 0
    [DataRow(1.0, 0.0, 0.0)]
    // arg(0+1i) = atan2(1, 0) = π/2
    [DataRow(0.0, 1.0, Math.PI / 2.0)]
    // arg(-1+0i) = atan2(0, -1) = π
    [DataRow(-1.0, 0.0, Math.PI)]
    // arg(1+1i) = atan2(1, 1) = π/4
    [DataRow(1.0, 1.0, Math.PI / 4.0)]
    public void Phase_KnownAngles_ReturnsAtan2OfImagOverReal(double real, double imag, double expected)
    {
        var z = new Complex(real, imag);

        // atan2(imag, real) — transcendental, use 1e-6 delta.
        Assert.AreEqual(expected, z.Phase, 1e-6);
    }

    [TestMethod]
    public void Conjugate_NegatesImaginaryPart_LeavesRealUnchanged()
    {
        // conj(3 + 4i) = 3 - 4i
        var z = new Complex(3.0, 4.0).Conjugate;

        Assert.AreEqual(3.0, z.Real, 1e-9);
        Assert.AreEqual(-4.0, z.Imag, 1e-9);
    }

    [TestMethod]
    public void FromPolar_MagnitudeAndPhase_ReconstructsRectangularForm()
    {
        // FromPolar(2, π/2) = (2·cos(π/2), 2·sin(π/2)) = (2·0, 2·1) = 0 + 2i
        var z = Complex.FromPolar(2.0, Math.PI / 2.0);

        Assert.AreEqual(0.0, z.Real, 1e-6);
        Assert.AreEqual(2.0, z.Imag, 1e-6);
    }

    [TestMethod]
    public void FromPolar_RoundTripsWithMagnitudeAndPhase()
    {
        // Building from a known (r, θ) and reading back |z| and arg(z) recovers the inputs.
        double r = 5.0;
        double theta = Math.PI / 6.0; // 30°

        var z = Complex.FromPolar(r, theta);

        Assert.AreEqual(r, z.Magnitude, 1e-6);
        Assert.AreEqual(theta, z.Phase, 1e-6);
    }

    [TestMethod]
    public void Exp_PurelyReal_MatchesRealExponential()
    {
        // exp(1 + 0i) = e^1·(cos 0 + i·sin 0) = e·(1 + 0i) = (e, 0)
        var z = Complex.Exp(new Complex(1.0, 0.0));

        Assert.AreEqual(Math.E, z.Real, 1e-6);
        Assert.AreEqual(0.0, z.Imag, 1e-6);
    }

    [TestMethod]
    public void Exp_ImaginaryPi_YieldsEulerIdentity()
    {
        // exp(0 + πi) = e^0·(cos π + i·sin π) = 1·(-1 + 0i) = -1 + 0i  (Euler's identity)
        var z = Complex.Exp(new Complex(0.0, Math.PI));

        Assert.AreEqual(-1.0, z.Real, 1e-6);
        Assert.AreEqual(0.0, z.Imag, 1e-6);
    }
}
