using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.MachineLearning.Regression;

namespace MOSAIC.Tests.Components.MachineLearning.Regression;

/// <summary>
/// Known-answer tests for <see cref="RidgeRegression"/>: online ridge regression via
/// Sherman-Morrison rank-1 updates.
///
/// Model: ŷ = Wᵀx, where W = Ainv·B.
/// Initial state: Ainv = (1/λ)·I, B = 0, W = 0.
/// After one update with (x, y):  A = λI + x·xᵀ,  B = x·yᵀ,  W = Ainv·B = A⁻¹·(x·yᵀ).
/// (The Sherman-Morrison recursion reproduces the closed-form A⁻¹ exactly, so expecteds
///  below are derived from that closed form.)
/// </summary>
[TestClass]
public class RidgeRegressionTests
{
    [TestMethod]
    public void Predict_FreshModel_ReturnsZeros()
    {
        // W starts as the zero matrix, so ŷ = Wᵀx = 0 for any x, in every output slot.
        var model = new RidgeRegression(inputDim: 2, outputDim: 1, lambda: 1.0);
        var x = Vector<double>.Build.Dense(new[] { 4.0, 7.0 });

        var prediction = model.Predict(x);

        Assert.AreEqual(1, prediction.Count);
        Assert.AreEqual(0.0, prediction[0], 1e-6);
    }

    [TestMethod]
    public void Predict_FreshModelMultiOutput_ReturnsZeroVector()
    {
        // Zero W => zero prediction regardless of output dimensionality.
        var model = new RidgeRegression(inputDim: 3, outputDim: 2, lambda: 5.0);
        var x = Vector<double>.Build.Dense(new[] { 1.0, -2.0, 3.0 });

        var prediction = model.Predict(x);

        Assert.AreEqual(2, prediction.Count);
        Assert.AreEqual(0.0, prediction[0], 1e-6);
        Assert.AreEqual(0.0, prediction[1], 1e-6);
    }

    [TestMethod]
    public void Update_Then_Predict_Dim1x1_ReproducesHandComputedValue()
    {
        // inputDim=1, outputDim=1, λ=2. Train on x=[3], y=[5].
        // A = λ + x² = 2 + 9 = 11  =>  Ainv = 1/11.
        // B = x·y = 3·5 = 15.
        // W = Ainv·B = 15/11.
        // Predict(x=[3]) = W·x = (15/11)·3 = 45/11 = 4.0909090909...
        var model = new RidgeRegression(inputDim: 1, outputDim: 1, lambda: 2.0);
        var x = Vector<double>.Build.Dense(new[] { 3.0 });
        var y = Vector<double>.Build.Dense(new[] { 5.0 });

        model.Update(x, y);
        var prediction = model.Predict(x);

        Assert.AreEqual(1, prediction.Count);
        Assert.AreEqual(45.0 / 11.0, prediction[0], 1e-6); // = 4.0909090909...
    }

    [TestMethod]
    public void Update_Then_Predict_Dim2x1_ReproducesHandComputedValue()
    {
        // inputDim=2, outputDim=1, λ=1. Train on x=[1,2], y=[3].
        // A = λI + x·xᵀ = [[1,0],[0,1]] + [[1,2],[2,4]] = [[2,2],[2,5]].  det = 10 - 4 = 6.
        // Ainv = (1/6)·[[5,-2],[-2,2]].
        // B = x·y = [3, 6]ᵀ.
        // W = Ainv·B = (1/6)·[5·3 - 2·6, -2·3 + 2·6]ᵀ = (1/6)·[3, 6]ᵀ = [0.5, 1.0]ᵀ.
        // Predict(x=[1,2]) = Wᵀx = 0.5·1 + 1.0·2 = 2.5.
        var model = new RidgeRegression(inputDim: 2, outputDim: 1, lambda: 1.0);
        var x = Vector<double>.Build.Dense(new[] { 1.0, 2.0 });
        var y = Vector<double>.Build.Dense(new[] { 3.0 });

        model.Update(x, y);
        var prediction = model.Predict(x);

        Assert.AreEqual(1, prediction.Count);
        Assert.AreEqual(2.5, prediction[0], 1e-6);
    }

    [TestMethod]
    public void Reset_AfterUpdate_ReturnsToZeroWeights()
    {
        // After training, Reset restores W = 0, so ŷ = Wᵀx = 0 again.
        var model = new RidgeRegression(inputDim: 2, outputDim: 1, lambda: 1.0);
        var x = Vector<double>.Build.Dense(new[] { 1.0, 2.0 });
        var y = Vector<double>.Build.Dense(new[] { 3.0 });
        model.Update(x, y);

        model.Reset();
        var prediction = model.Predict(x);

        Assert.AreEqual(1, prediction.Count);
        Assert.AreEqual(0.0, prediction[0], 1e-6);
    }
}
