using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models;
using MOSAIC.Tests.TestSupport;

namespace MOSAIC.Tests.Models.SignalProcessing;

/// <summary>
/// Known-answer tests for <see cref="AdaptiveFilterBlock"/>: an adaptive first-order IIR
/// low-pass filter.
///
/// Per sample, per channel (with sampling frequency fs = desiredRate):
///   dx        = (x[k] − x[k−1]) · fs
///   dxFilt    = αd · dx + (1 − αd) · dxFilt[k−1],   αd = (2π·α/fs)/(1 + 2π·α/fs), α = Alpha/10
///   fc        = exp(Offset + Deriviate·|dxFilt| + Magnitude·|x[k]|)
///   a         = (2π·fc/fs)/(1 + 2π·fc/fs)
///   y[k]      = a · x[k] + (1 − a) · y[k−1]
/// State (prevInput, prevDerivative, prevOutput) starts at 0.
///
/// Choosing Alpha = 0 makes αd = 0, so dxFilt = 0·dx + 1·dxFilt[k−1] = 0 forever
/// (it starts at 0). That removes the derivative path and makes fc depend only on
/// Offset and Magnitude·|x|, which lets the expecteds below be derived in closed form.
/// </summary>
[TestClass]
public class AdaptiveFilterBlockTests
{
    [TestMethod]
    public void Process_ZeroExponentTerms_FirstSample_AppliesOnePoleGain()
    {
        // Alpha=0 => dxFilt=0. Offset=Magnitude=Deriviate=0 => fc = exp(0) = 1 Hz.
        // fs = 1: a = (2π·1/1)/(1 + 2π·1/1) = 2π/(1+2π).
        // prevOutput starts 0 => y = a·x = a·2.
        using var block = new AdaptiveFilterBlock(
            name: "AF", desiredRate: 1.0, alphaUi: 0.0, offset: 0.0, magnitude: 0.0, deriviate: 0.0);
        var x = Vector<double>.Build.Dense(new[] { 2.0 });

        var y = block.Process(x);

        Assert.AreEqual(1, y.Count);
        double a = (2 * Math.PI) / (1 + 2 * Math.PI); // fc=1, fs=1
        Assert.AreEqual(a * 2.0, y[0], 1e-6);          // = 1.72539487...
    }

    [TestMethod]
    public void Process_ZeroExponentTerms_UpdatesCutoffFrequencyToOne()
    {
        // fc of the first channel is exposed via CutoffFrequency. With all exponent
        // terms zero, fc = exp(0) = 1 Hz exactly.
        using var block = new AdaptiveFilterBlock(
            name: "AF", desiredRate: 1.0, alphaUi: 0.0, offset: 0.0, magnitude: 0.0, deriviate: 0.0);
        var x = Vector<double>.Build.Dense(new[] { 2.0 });

        block.Process(x);

        Assert.AreEqual(1.0, block.CutoffFrequency, 1e-9); // exp(0) = 1
    }

    [TestMethod]
    public void Process_SecondSample_RecursesOnPreviousOutput()
    {
        // Same setup (a = 2π/(1+2π)); feed x=2 twice.
        // y1 = a·2 (prevOut 0). y2 = a·2 + (1−a)·y1.  Alpha=0 keeps dxFilt=0 and fc=1
        // for both samples, so 'a' is identical on both steps.
        using var block = new AdaptiveFilterBlock(
            name: "AF", desiredRate: 1.0, alphaUi: 0.0, offset: 0.0, magnitude: 0.0, deriviate: 0.0);
        var x = Vector<double>.Build.Dense(new[] { 2.0 });

        block.Process(x);
        var y2 = block.Process(x);

        double a = (2 * Math.PI) / (1 + 2 * Math.PI);
        double y1 = a * 2.0;
        double expected = a * 2.0 + (1 - a) * y1; // = 1.96229601...
        Assert.AreEqual(1, y2.Count);
        Assert.AreEqual(expected, y2[0], 1e-6);
    }

    [TestMethod]
    public void Process_OffsetShiftsCutoffExponent()
    {
        // Alpha=0, Magnitude=Deriviate=0, Offset=1 => fc = exp(1) = e.
        // fs=1: a = (2π·e)/(1 + 2π·e).  y = a·x (prevOut 0).
        using var block = new AdaptiveFilterBlock(
            name: "AF", desiredRate: 1.0, alphaUi: 0.0, offset: 1.0, magnitude: 0.0, deriviate: 0.0);
        var x = Vector<double>.Build.Dense(new[] { 2.0 });

        var y = block.Process(x);

        double fc = Math.Exp(1.0);                         // e
        double a = (2 * Math.PI * fc) / (1 + 2 * Math.PI * fc);
        Assert.AreEqual(fc, block.CutoffFrequency, 1e-6);  // e = 2.71828...
        Assert.AreEqual(a * 2.0, y[0], 1e-6);              // = 1.88937727...
    }

    [TestMethod]
    public void Process_MagnitudeTermScalesCutoffByAbsInput()
    {
        // Alpha=0, Offset=Deriviate=0, Magnitude=-2, x=3 => fc = exp(-2·|3|) = exp(-6).
        // A small fc yields a small 'a' and thus a heavily attenuated first output.
        using var block = new AdaptiveFilterBlock(
            name: "AF", desiredRate: 1.0, alphaUi: 0.0, offset: 0.0, magnitude: -2.0, deriviate: 0.0);
        var x = Vector<double>.Build.Dense(new[] { 3.0 });

        var y = block.Process(x);

        double fc = Math.Exp(-2.0 * 3.0);                  // exp(-6) = 0.00247875...
        double a = (2 * Math.PI * fc) / (1 + 2 * Math.PI * fc);
        Assert.AreEqual(fc, block.CutoffFrequency, 1e-6);
        Assert.AreEqual(a * 3.0, y[0], 1e-6);              // = 0.04600684...
    }

    [TestMethod]
    public void Process_SamplingRateEntersOnePoleCoefficient()
    {
        // fc=1 (all exponent terms 0), fs=2: 2π·fc/fs = π.
        // a = π/(1+π).  y = a·x = a·4 (prevOut 0).
        using var block = new AdaptiveFilterBlock(
            name: "AF", desiredRate: 2.0, alphaUi: 0.0, offset: 0.0, magnitude: 0.0, deriviate: 0.0);
        var x = Vector<double>.Build.Dense(new[] { 4.0 });

        var y = block.Process(x);

        double a = Math.PI / (1 + Math.PI); // 2π·1/2 = π
        Assert.AreEqual(a * 4.0, y[0], 1e-6); // = 3.03418797...
    }

    [TestMethod]
    public void Process_MultiChannel_FiltersEachColumnIndependently()
    {
        // Two channels, first sample, all exponent terms 0, fs=1 => a = 2π/(1+2π) both.
        // y[c] = a·x[c] with prevOut 0.  CutoffFrequency reflects only channel 0's fc (=1).
        using var block = new AdaptiveFilterBlock(
            name: "AF", desiredRate: 1.0, alphaUi: 0.0, offset: 0.0, magnitude: 0.0, deriviate: 0.0);
        var x = Vector<double>.Build.Dense(new[] { 2.0, 5.0 });

        var y = block.Process(x);

        double a = (2 * Math.PI) / (1 + 2 * Math.PI);
        Assert.AreEqual(2, y.Count);
        Assert.AreEqual(a * 2.0, y[0], 1e-6);
        Assert.AreEqual(a * 5.0, y[1], 1e-6);
        Assert.AreEqual(1.0, block.CutoffFrequency, 1e-9); // channel 0 fc = exp(0) = 1
    }

    [TestMethod]
    public void OnReceive_VectorInput_PublishesFilteredVector()
    {
        // Drive the block end to end through its async publish pump to confirm a Vector
        // input yields a published Vector equal to Process(x).
        using var block = new AdaptiveFilterBlock(
            name: "AF", desiredRate: 1.0, alphaUi: 0.0, offset: 0.0, magnitude: 0.0, deriviate: 0.0);
        var x = Vector<double>.Build.Dense(new[] { 2.0 });

        var published = BlockHarness.CaptureVector(block, x);

        double a = (2 * Math.PI) / (1 + 2 * Math.PI);
        Assert.AreEqual(1, published.Count);
        Assert.AreEqual(a * 2.0, published[0], 1e-6); // same closed form as Process
    }

    [TestMethod]
    public void OnReceive_UnsupportedType_ThrowsInvalidOperation()
    {
        // OnReceive only accepts Vector<double> / Matrix<double>; anything else throws.
        using var block = new AdaptiveFilterBlock(name: "AF", desiredRate: 1.0);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => block.ReceiveInput(sender: block, value: "not a vector"));
    }
}
