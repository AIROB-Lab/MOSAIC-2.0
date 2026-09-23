using System;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Models.Devices;
using MOSAIC.Models.Tests;

namespace MOSAIC.Tests.Models.Tests.TACTest;

/// <summary>
/// Known-answer tests for the deterministic distance path of <see cref="TAC"/>.
///
/// <para>
/// TAC exposes no public <c>Evaluate</c>, so the block is driven through its real
/// <see cref="MOSAIC.Components.Basics.BaseBlock.ReceiveInput"/> entry point:
/// </para>
/// <list type="bullet">
///   <item><description>A <see cref="Stimulus"/> sender carrying a <c>(string state, Vector)</c>
///     tuple sets the <em>target</em> (this is the only sender type that writes the target).</description></item>
///   <item><description>Any non-Stimulus sender carrying a <see cref="Vector{T}"/> sets the
///     <em>prediction</em>.</description></item>
/// </list>
/// <para>
/// Once both target and prediction are present, <c>Evaluate</c> runs synchronously inside
/// <c>OnReceive</c> and assigns the observable <see cref="TAC.DistanceToTarget"/> =
/// <c>‖target − prediction‖₂</c> before returning, so the property can be read directly.
/// The Stimulus is only a well-typed sender here; its default state is "rest" (never "capture"),
/// so no trial/dwell/clock logic is entered and the distance computation is fully deterministic.
/// Every expected value is the Euclidean norm derived by hand from the definition.
/// </para>
/// </summary>
[TestClass]
public class TACTests
{
    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    /// <summary>
    /// Drives the deterministic distance path once: feeds <paramref name="target"/> from a Stimulus
    /// sender, then <paramref name="prediction"/> from a non-Stimulus sender, and returns the
    /// resulting <see cref="TAC.DistanceToTarget"/>.
    /// </summary>
    private static double DriveDistance(Vector<double> target, Vector<double> prediction)
    {
        using var stimulus = new Stimulus(name: "Stim");
        using var tac = new TAC(name: "TAC", desiredRate: 0, stimulusBlockName: "Stim");

        // Stimulus sender + (state, target) tuple => sets _target (prediction still null => no Evaluate yet).
        tac.ReceiveInput(sender: stimulus, value: (stimulus.State, target));

        // Non-Stimulus sender (the TAC itself) + Vector => sets _prediction => Evaluate runs synchronously.
        tac.ReceiveInput(sender: tac, value: prediction);

        return tac.DistanceToTarget;
    }

    [TestMethod]
    public void Evaluate_OrthogonalUnitVectors_DistanceIsSqrt2()
    {
        // target=[1,0,0], prediction=[0,1,0].
        // ‖[1,-1,0]‖₂ = sqrt(1² + (-1)² + 0²) = sqrt(2) ≈ 1.4142135623.
        var target = Vec(1.0, 0.0, 0.0);
        var prediction = Vec(0.0, 1.0, 0.0);

        var distance = DriveDistance(target, prediction);

        Assert.AreEqual(Math.Sqrt(2.0), distance, 1e-9); // sqrt(2)
    }

    [TestMethod]
    public void Evaluate_IdenticalVectors_DistanceIsZero()
    {
        // target == prediction => difference is the zero vector => ‖0‖₂ = 0.
        var target = Vec(0.5, -0.25, 0.75);
        var prediction = Vec(0.5, -0.25, 0.75);

        var distance = DriveDistance(target, prediction);

        Assert.AreEqual(0.0, distance, 1e-9); // exact zero
    }

    [TestMethod]
    public void Evaluate_ThreeFourFiveTriangle_DistanceIsFive()
    {
        // target=[0,0], prediction=[3,4]. ‖[-3,-4]‖₂ = sqrt(9 + 16) = sqrt(25) = 5.
        var target = Vec(0.0, 0.0);
        var prediction = Vec(3.0, 4.0);

        var distance = DriveDistance(target, prediction);

        Assert.AreEqual(5.0, distance, 1e-9); // 3-4-5 right triangle
    }

    [TestMethod]
    public void Evaluate_SingleAxisOffset_DistanceIsComponentMagnitude()
    {
        // target=[1,0,0,0], prediction=[0,0,0,0]. ‖[1,0,0,0]‖₂ = sqrt(1) = 1.
        var target = Vec(1.0, 0.0, 0.0, 0.0);
        var prediction = Vec(0.0, 0.0, 0.0, 0.0);

        var distance = DriveDistance(target, prediction);

        Assert.AreEqual(1.0, distance, 1e-9); // unit offset on one axis
    }

    [TestMethod]
    public void Evaluate_MismatchedLengths_TruncatesToShorterThenComputesDistance()
    {
        // Length-alignment guard: target has 3 elements, prediction has 2 => both truncated to n=2.
        // Effective target=[1,2], prediction=[4,6]. ‖[-3,-4]‖₂ = sqrt(9 + 16) = 5.
        var target = Vec(1.0, 2.0, 3.0);
        var prediction = Vec(4.0, 6.0);

        var distance = DriveDistance(target, prediction);

        Assert.AreEqual(5.0, distance, 1e-9); // trailing target component (3) dropped before the norm
    }

    [DataTestMethod]
    [DataRow(3.0, 7.0, 4.0)]   // ‖[3-7]‖ = |−4| = 4
    [DataRow(0.0, 0.0, 0.0)]   // ‖[0]‖   = 0
    [DataRow(-2.0, 5.0, 7.0)]  // ‖[−2−5]‖ = |−7| = 7
    [DataRow(2.5, 1.0, 1.5)]   // ‖[2.5−1]‖ = |1.5| = 1.5
    public void Evaluate_ScalarVectors_DistanceIsAbsoluteDifference(double target, double prediction, double expected)
    {
        // For length-1 vectors the L2 norm collapses to |target − prediction|.
        var distance = DriveDistance(Vec(target), Vec(prediction));

        Assert.AreEqual(expected, distance, 1e-9);
    }

    [TestMethod]
    public void Constructor_ExplicitParams_ExposedOnProperties()
    {
        // The constructor stores its dwell/threshold/name arguments verbatim on the public surface.
        using var tac = new TAC(name: "TAC", desiredRate: 0, stimulusBlockName: "MyStim",
            dwellTime: 3.5, successThreshold: 0.15);

        Assert.AreEqual(3.5, tac.DwellTime, 1e-9);
        Assert.AreEqual(0.15, tac.SuccessThreshold, 1e-9);
        Assert.AreEqual("MyStim", tac.StimulusBlockName);
    }

    [TestMethod]
    public void Constructor_DefaultParams_MatchDocumentedDefaults()
    {
        // Documented defaults: dwellTime = 2.0 s, successThreshold = 0.2.
        using var tac = new TAC(name: "TAC", desiredRate: 0, stimulusBlockName: "Stim");

        Assert.AreEqual(2.0, tac.DwellTime, 1e-9);
        Assert.AreEqual(0.2, tac.SuccessThreshold, 1e-9);
    }

    [TestMethod]
    public void BindStimulus_Null_ThrowsArgumentNullException()
    {
        // Guard: first BindStimulus call with null hits `stimulus ?? throw` (the early-return guard
        // only triggers once a stimulus is already bound, which is not the case here).
        using var tac = new TAC(name: "TAC", desiredRate: 0, stimulusBlockName: "Stim");

        Assert.ThrowsExactly<ArgumentNullException>(() => tac.BindStimulus(null!));
    }
}
