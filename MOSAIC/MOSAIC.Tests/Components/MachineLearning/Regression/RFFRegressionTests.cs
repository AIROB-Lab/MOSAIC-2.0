using System;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.MachineLearning.Regression;

namespace MOSAIC.Tests.Components.MachineLearning.Regression;

/// <summary>
/// Known-answer tests for <see cref="RFFRegression"/>: a Random Fourier Features wrapper around
/// an online ridge regressor.
///
/// Feature map: φ(x) = cos(Ω·x + β), where Ω (featureDim × inputDim) is drawn from N(0, σ) and
/// β (featureDim) from U(-π, π), both from a single <c>System.Random(seed)</c> stream. Predictions
/// are produced by the inner ridge model operating in that feature space: ŷ = Wᵀ·φ(x).
///
/// The inner ridge starts with W = 0, so a fresh model predicts the zero vector for any input.
/// A single Update(x, y) trains the inner ridge on the pair (φ(x), y); because Ω and β are fixed
/// by the seed, the whole transform — and therefore every prediction — is deterministic.
/// </summary>
[TestClass]
public class RFFRegressionTests
{
    [TestMethod]
    public void Predict_FreshModel_ReturnsZeroVector()
    {
        // No Update yet => inner ridge weights W = 0 => ŷ = Wᵀ·φ(x) = 0 in every output slot.
        var model = new RFFRegression(
            inputDim: 3, outputDim: 2, lambda: 1.0, sigma: 1.0, featureDim: 32, seed: 42);
        var x = Vector<double>.Build.Dense(new[] { 1.0, -2.0, 0.5 });

        var prediction = model.Predict(x);

        Assert.AreEqual(2, prediction.Count);         // output dimension carries through from ctor
        Assert.AreEqual(0.0, prediction[0], 1e-6);
        Assert.AreEqual(0.0, prediction[1], 1e-6);
    }

    [TestMethod]
    public void Predict_FeatureDimOne_ReproducesHandComputedRidgeValue()
    {
        // featureDim=1 makes φ(x) a single scalar cos(Ω·x + β), so the inner ridge is 1×1.
        //
        // Recreate the EXACT feature draw the ctor makes: one Random(seed) stream feeds first the
        // Ω sample (Normal), then the β sample (Uniform), in that order.
        const int seed = 7;
        const double sigma = 1.0;
        const double lambda = 2.0;
        var rng = new Random(seed);
        var omega = Matrix<double>.Build.Random(1, 2, new Normal(0.0, sigma, rng)); // Ω : 1×2
        var beta = Vector<double>.Build.Random(1, new ContinuousUniform(-Math.PI, Math.PI, rng)); // β : 1

        var x = Vector<double>.Build.Dense(new[] { 0.3, -0.4 });
        double phi = Math.Cos((omega * x + beta)[0]); // φ(x) = cos(Ω·x + β), a scalar

        // Inner ridge (inputDim=1 in feature space) after Update(φ, y=[c]):
        //   A = λ + φ²  =>  Ainv = 1/(λ+φ²);  B = φ·c;  W = φ·c/(λ+φ²).
        //   Predict(x) = W·φ = φ²·c/(λ+φ²).
        const double c = 5.0;
        double expected = phi * phi * c / (lambda + phi * phi);

        var model = new RFFRegression(
            inputDim: 2, outputDim: 1, lambda: lambda, sigma: sigma, featureDim: 1, seed: seed);
        model.Update(x, Vector<double>.Build.Dense(new[] { c }));
        var prediction = model.Predict(x);

        Assert.AreEqual(1, prediction.Count);
        Assert.AreEqual(expected, prediction[0], 1e-6);
    }

    [TestMethod]
    public void Update_ThenPredict_ChangesPredictionAwayFromZero()
    {
        // Training on (x, y) with a nonzero target must move the trained-point prediction off zero.
        // (φ(x) is a.s. nonzero, so φ²·y/(λ+φ²) ≠ 0 in the 1-D argument, and analogously here.)
        var model = new RFFRegression(
            inputDim: 4, outputDim: 1, lambda: 1.0, sigma: 1.0, featureDim: 64, seed: 123);
        var x = Vector<double>.Build.Dense(new[] { 0.5, -1.0, 2.0, 0.25 });
        var y = Vector<double>.Build.Dense(new[] { 3.0 });

        model.Update(x, y);
        var prediction = model.Predict(x);

        Assert.AreEqual(1, prediction.Count);
        Assert.IsTrue(Math.Abs(prediction[0]) > 1e-6,
            $"Prediction should move off zero after training; got {prediction[0]}.");
    }

    [TestMethod]
    public void Reset_AfterUpdate_ReturnsToZeroVector()
    {
        // Reset delegates to the inner ridge Reset, restoring W = 0 => ŷ = 0 again.
        var model = new RFFRegression(
            inputDim: 3, outputDim: 2, lambda: 1.0, sigma: 1.0, featureDim: 32, seed: 42);
        var x = Vector<double>.Build.Dense(new[] { 1.0, -2.0, 0.5 });
        var y = Vector<double>.Build.Dense(new[] { 4.0, -1.0 });
        model.Update(x, y);

        model.Reset();
        var prediction = model.Predict(x);

        Assert.AreEqual(2, prediction.Count);
        Assert.AreEqual(0.0, prediction[0], 1e-6);
        Assert.AreEqual(0.0, prediction[1], 1e-6);
    }

    [TestMethod]
    public void Predict_SameSeedSameTraining_ProducesIdenticalPredictions()
    {
        // Reproducibility: identical seed => identical Ω, β => identical φ => identical inner
        // training => equal predictions element-for-element.
        var a = new RFFRegression(
            inputDim: 3, outputDim: 2, lambda: 1.0, sigma: 0.7, featureDim: 48, seed: 99);
        var b = new RFFRegression(
            inputDim: 3, outputDim: 2, lambda: 1.0, sigma: 0.7, featureDim: 48, seed: 99);
        var x = Vector<double>.Build.Dense(new[] { 0.2, 1.5, -0.8 });
        var y = Vector<double>.Build.Dense(new[] { 2.0, -3.0 });

        a.Update(x, y);
        b.Update(x, y);
        var pa = a.Predict(x);
        var pb = b.Predict(x);

        Assert.AreEqual(pb.Count, pa.Count);
        Assert.AreEqual(2, pa.Count);
        Assert.AreEqual(pb[0], pa[0], 1e-6);
        Assert.AreEqual(pb[1], pa[1], 1e-6);
    }

    [TestMethod]
    public void Predict_DifferentSeeds_ProduceDifferentFeatureTransforms()
    {
        // Different seeds draw different Ω, β, so the same training pair generally maps to a
        // different trained-point prediction. (Guards that the seed actually drives the transform.)
        var a = new RFFRegression(
            inputDim: 3, outputDim: 1, lambda: 1.0, sigma: 1.0, featureDim: 64, seed: 1);
        var b = new RFFRegression(
            inputDim: 3, outputDim: 1, lambda: 1.0, sigma: 1.0, featureDim: 64, seed: 2);
        var x = Vector<double>.Build.Dense(new[] { 1.0, 2.0, 3.0 });
        var y = Vector<double>.Build.Dense(new[] { 5.0 });

        a.Update(x, y);
        b.Update(x, y);
        var pa = a.Predict(x);
        var pb = b.Predict(x);

        Assert.AreEqual(1, pa.Count);
        Assert.AreEqual(1, pb.Count);
        Assert.IsTrue(Math.Abs(pa[0] - pb[0]) > 1e-6,
            $"Different seeds should yield different predictions; got {pa[0]} vs {pb[0]}.");
    }

    [TestMethod]
    public void Update_WrongInputDimension_Throws()
    {
        // Guard: input length must equal InputDimension (validated before the φ transform).
        var model = new RFFRegression(
            inputDim: 3, outputDim: 1, lambda: 1.0, sigma: 1.0, featureDim: 16, seed: 42);
        var wrong = Vector<double>.Build.Dense(new[] { 1.0, 2.0 }); // length 2, expected 3
        var y = Vector<double>.Build.Dense(new[] { 1.0 });

        Assert.ThrowsExactly<ArgumentException>(() => model.Update(wrong, y));
    }

    [TestMethod]
    public void Constructor_NonPositiveFeatureDim_Throws()
    {
        // Guard: featureDim must be strictly positive.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new RFFRegression(inputDim: 2, outputDim: 1, lambda: 1.0, sigma: 1.0, featureDim: 0, seed: 42));
    }
}
