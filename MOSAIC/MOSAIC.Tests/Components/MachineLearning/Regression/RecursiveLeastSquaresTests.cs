using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.MachineLearning.Regression;

namespace MOSAIC.Tests.Components.MachineLearning.Regression;

/// <summary>
/// Known-answer tests for <see cref="RecursiveLeastSquares"/>, online RLS regression with a
/// forgetting factor. All expected values are derived by hand from the RLS recursion:
///   init:  P = (1/δ) I,   W = 0
///   gain:  k = P x / (ff + xᵀ P x)
///   error: e = y − Wᵀ x
///   W  ← W + k eᵀ
///   P  ← (1/ff) (P − k xᵀ P)
///   Predict(x)    = Wᵀ x
///   Confidence(x) = xᵀ P x
/// </summary>
[TestClass]
public class RecursiveLeastSquaresTests
{
    [TestMethod]
    public void Constructor_StoresDimensionsAndHyperparameters()
    {
        var rls = new RecursiveLeastSquares(inputDim: 3, outputDim: 2, delta: 2.0, forgettingFactor: 0.95);

        Assert.AreEqual(3, rls.InputDimension);
        Assert.AreEqual(2, rls.OutputDimension);
        Assert.AreEqual(2.0, rls.Delta, 1e-12);
        Assert.AreEqual(0.95, rls.ForgettingFactor, 1e-12);
    }

    [TestMethod]
    public void Predict_FreshModel_ReturnsZeros()
    {
        // W starts as the zero matrix, so ŷ = Wᵀx = 0 for any x.
        var rls = new RecursiveLeastSquares(inputDim: 2, outputDim: 1);
        var x = Vector<double>.Build.Dense(new[] { 1.0, 2.0 });

        var prediction = rls.Predict(x);

        Assert.AreEqual(1, prediction.Count);
        Assert.AreEqual(0.0, prediction[0], 1e-9);
    }

    [TestMethod]
    public void Confidence_FreshModel_EqualsInverseDeltaWeightedNormSquared()
    {
        // Fresh P = (1/δ) I with δ=2. Confidence(x) = xᵀ P x = (1/2)(1² + 2²) = 2.5.
        var rls = new RecursiveLeastSquares(inputDim: 2, outputDim: 1, delta: 2.0);
        var x = Vector<double>.Build.Dense(new[] { 1.0, 2.0 });

        Assert.AreEqual(2.5, rls.Confidence(x), 1e-9);
    }

    [TestMethod]
    public void UpdateThenPredict_Dim1x1_ReproducesHandComputedValue()
    {
        // δ=1, ff=1  ⇒  P₀ = 1, W₀ = 0. Train on x=[2], y=[4].
        //   Px    = 2
        //   denom = ff + xᵀPx = 1 + (2·1·2) = 5
        //   k     = Px/denom  = 2/5 = 0.4
        //   e     = y − Wᵀx   = 4 − 0 = 4
        //   W     = 0 + k·e   = 0.4·4 = 1.6
        // Predict(x=[2]) = W·x = 1.6·2 = 3.2 ;  Predict(x=[5]) = 1.6·5 = 8.0
        var rls = new RecursiveLeastSquares(inputDim: 1, outputDim: 1, delta: 1.0, forgettingFactor: 1.0);
        var x = Vector<double>.Build.Dense(new[] { 2.0 });
        var y = Vector<double>.Build.Dense(new[] { 4.0 });

        rls.Update(x, y);

        Assert.AreEqual(3.2, rls.Predict(x)[0], 1e-6);
        Assert.AreEqual(8.0, rls.Predict(Vector<double>.Build.Dense(new[] { 5.0 }))[0], 1e-6);
    }

    [TestMethod]
    public void UpdateThenPredict_Dim2x1_ReproducesHandComputedValue()
    {
        // δ=1, ff=1  ⇒  P₀ = I, W₀ = 0. Train on x=[1,2], y=[3].
        //   Px    = [1, 2]
        //   denom = 1 + (1·1 + 2·2) = 6
        //   k     = [1/6, 2/6]
        //   e     = 3
        //   W     = k·e = [0.5, 1.0]
        // Predict(x=[1,2]) = Wᵀx = 0.5·1 + 1.0·2 = 2.5
        var rls = new RecursiveLeastSquares(inputDim: 2, outputDim: 1, delta: 1.0, forgettingFactor: 1.0);
        var x = Vector<double>.Build.Dense(new[] { 1.0, 2.0 });
        var y = Vector<double>.Build.Dense(new[] { 3.0 });

        rls.Update(x, y);
        var prediction = rls.Predict(x);

        Assert.AreEqual(1, prediction.Count);
        Assert.AreEqual(2.5, prediction[0], 1e-6);
    }

    [TestMethod]
    public void Reset_AfterUpdate_ReturnsToZeroWeights()
    {
        var rls = new RecursiveLeastSquares(inputDim: 1, outputDim: 1, delta: 1.0, forgettingFactor: 1.0);
        var x = Vector<double>.Build.Dense(new[] { 2.0 });
        rls.Update(x, Vector<double>.Build.Dense(new[] { 4.0 }));

        rls.Reset();

        Assert.AreEqual(0.0, rls.Predict(x)[0], 1e-9);
        // Confidence also returns to xᵀP₀x = 2·(1/δ)·2 = 4 with δ=1.
        Assert.AreEqual(4.0, rls.Confidence(x), 1e-9);
    }

    [TestMethod]
    public void Constructor_ForgettingFactorAboveOne_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RecursiveLeastSquares(inputDim: 1, outputDim: 1, delta: 1.0, forgettingFactor: 1.5));
    }

    [TestMethod]
    public void Constructor_NonPositiveDelta_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RecursiveLeastSquares(inputDim: 1, outputDim: 1, delta: 0.0));
    }

    [TestMethod]
    public void Constructor_NonPositiveDimension_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RecursiveLeastSquares(inputDim: 0, outputDim: 1));
    }

    [TestMethod]
    public void Update_WrongInputDimension_Throws()
    {
        var rls = new RecursiveLeastSquares(inputDim: 2, outputDim: 1);
        var wrongX = Vector<double>.Build.Dense(new[] { 1.0 });      // expected length 2
        var y = Vector<double>.Build.Dense(new[] { 3.0 });

        Assert.ThrowsExactly<ArgumentException>(() => rls.Update(wrongX, y));
    }

    [TestMethod]
    public void Predict_WrongInputDimension_Throws()
    {
        var rls = new RecursiveLeastSquares(inputDim: 2, outputDim: 1);
        var wrongX = Vector<double>.Build.Dense(new[] { 1.0 });      // expected length 2

        Assert.ThrowsExactly<ArgumentException>(() => rls.Predict(wrongX));
    }
}
