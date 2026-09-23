using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Manager.ControlAlgorithm;

namespace MOSAIC.Tests.Components.Manager.ControlAlgorithm;

/// <summary>
/// Known-answer tests for <see cref="StepwiseControlStrategy"/>: incremental (stepwise) control with a
/// deadband threshold. The strategy is driven purely through its public surface
/// (<see cref="StepwiseControlStrategy.ProcessPrediction"/> and
/// <see cref="ControlStrategyBase.GetControlDict"/>), so no UI/hardware is required.
///
/// The private constants under test are re-declared here and derived from the source:
///   StepSize = 100 / 25.0 = 4.0   (max step per tick, scaled by how far the signal exceeds the deadband)
///   Deadband = 0.3                (activation threshold; a channel must be strictly greater to act)
///
/// Per-channel behaviour comes from the inherited <c>StepControl</c>/<c>Add</c> helpers:
///   if (positive > Deadband || negative > Deadband)
///       Add(doa, max(0, positive-Deadband)*StepSize - max(0, negative-Deadband)*StepSize)   // then clamp to [0,100]
///
/// Channel wiring (index -> role), read from ProcessPrediction:
///   fingers:  ThumbFlexion=0, ThumbRotation=1, Index=2, Middle=3, Ring=4, Little=5   (negative = open signal, index 12)
///   wrist:    WristFlexionExtension(+7,-6), WristPronationSupination(+9,-8), WristUlnarRadial(+10,-11)
///   elbow:    ElbowFlexion(+13, -open), ElbowExtension(+14, -open)
/// All arithmetic below is derived by hand from these definitions and noted on each assertion.
/// </summary>
[TestClass]
public class StepwiseControlStrategyTests
{
    private const double StepSize = 100 / 25.0; // = 4.0
    private const double Deadband = 0.3;

    /// <summary>
    /// Builds a length-15 prediction (covers every index the strategy reads, 0..14) with the given
    /// non-zero entries; all other channels are 0.
    /// </summary>
    private static Vector<double> Pred(params (int idx, double val)[] entries)
    {
        var v = Vector<double>.Build.Dense(15);
        foreach (var (idx, val) in entries) v[idx] = val;
        return v;
    }

    private static double Value(StepwiseControlStrategy s, DegreesOfActuation doa) => s.GetControlDict()[doa];

    // ---------------------------------------------------------------- Name / defaults

    [TestMethod]
    public void Name_ReturnsStepwiseControl()
    {
        var s = new StepwiseControlStrategy();

        Assert.AreEqual("StepwiseControl", s.Name);
    }

    [TestMethod]
    public void Constructor_InitializesExpectedDefaults()
    {
        // InitializeDefaults sets exactly 11 DOAs: the three wrist axes to 50 (neutral), the rest to 0.
        var s = new StepwiseControlStrategy();

        var dict = s.GetControlDict();

        Assert.AreEqual(11, dict.Count);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.ThumbFlexion], 1e-9);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.Index], 1e-9);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.Little], 1e-9);
        Assert.AreEqual(50.0, dict[DegreesOfActuation.WristFlexionExtension], 1e-9);
        Assert.AreEqual(50.0, dict[DegreesOfActuation.WristPronationSupination], 1e-9);
        Assert.AreEqual(50.0, dict[DegreesOfActuation.WristUlnarRadial], 1e-9);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.ElbowFlexion], 1e-9);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.ElbowExtension], 1e-9);
    }

    // ---------------------------------------------------------------- Empty-prediction guard

    [TestMethod]
    public void ProcessPrediction_EmptyPrediction_LeavesDefaultsUnchanged()
    {
        // Guard: `if (prediction.Count == 0) return;` => nothing is touched, defaults remain.
        var s = new StepwiseControlStrategy();
        var empty = Vector<double>.Build.Dense(0);

        s.ProcessPrediction(empty);

        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.Index), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
    }

    // ---------------------------------------------------------------- Single step above deadband

    [TestMethod]
    public void ProcessPrediction_FingerAboveDeadband_AddsScaledStep()
    {
        // Index positive channel = index 2 = 0.8; open (index 12) = 0.
        // StepControl(Index, positive=0.8, negative=0): 0.8 > 0.3 =>
        //   posStep = (0.8 - 0.3) * 4 = 0.5 * 4 = 2.0; negStep = max(0, 0 - 0.3) * 4 = 0.
        //   Add(Index, 2.0) from default 0 => Clamp(0 + 2, 0, 100) = 2.0.
        var s = new StepwiseControlStrategy();

        s.ProcessPrediction(Pred((2, 0.8)));

        Assert.AreEqual(2.0, Value(s, DegreesOfActuation.Index), 1e-9);
        // Wrist channels (6..11) are all 0 => StepControl guard false => wrist stays at neutral 50.
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
        // ThumbFlexion positive (idx 0) = 0 and open = 0 => guard false => stays 0.
        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.ThumbFlexion), 1e-9);
    }

    // ---------------------------------------------------------------- Deadband threshold (guard)

    [DataTestMethod]
    [DataRow(0.30)] // exactly at the deadband: `0.30 > 0.30` is false (strict) => no action
    [DataRow(0.25)] // below the deadband => no action
    public void ProcessPrediction_FingerInputNotAboveDeadband_LeavesValueUnchanged(double activation)
    {
        // positive = activation (<= Deadband), negative/open = 0 => guard `(pos > db || neg > db)` is false
        // => StepControl performs no Add => Index remains at its default of 0.
        var s = new StepwiseControlStrategy();

        s.ProcessPrediction(Pred((2, activation)));

        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.Index), 1e-9);
    }

    // ---------------------------------------------------------------- Accumulation + open (negative) signal

    [TestMethod]
    public void ProcessPrediction_AccumulatesThenOpenSignalDecrements()
    {
        // Each activation of Index (idx 2 = 0.8) adds (0.8 - 0.3) * 4 = 2.0 to the accumulated value.
        var s = new StepwiseControlStrategy();

        for (var i = 0; i < 5; i++)
            s.ProcessPrediction(Pred((2, 0.8)));

        // After 5 incremental steps: 5 * 2.0 = 10.0 (proves stepwise accumulation, not a jump-to-target).
        Assert.AreEqual(10.0, Value(s, DegreesOfActuation.Index), 1e-9);

        // Now an open signal (index 12 = 0.8) with the finger channel back at 0:
        // StepControl(Index, positive=0, negative=0.8): posStep = 0, negStep = (0.8 - 0.3) * 4 = 2.0.
        // Add(Index, 0 - 2.0 = -2.0) => Clamp(10 - 2, 0, 100) = 8.0.
        s.ProcessPrediction(Pred((12, 0.8)));

        Assert.AreEqual(8.0, Value(s, DegreesOfActuation.Index), 1e-9);
    }

    // ---------------------------------------------------------------- Wrist opposing pair (both directions)

    [TestMethod]
    public void ProcessPrediction_WristOpposingPair_StepsUpAndDown()
    {
        // WristFlexionExtension: positive = index 7, negative = index 6; default = 50.
        var s = new StepwiseControlStrategy();

        // Positive channel 0.8 => posStep = (0.8 - 0.3) * 4 = 2.0 => 50 + 2 = 52.0.
        s.ProcessPrediction(Pred((7, 0.8)));
        Assert.AreEqual(52.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);

        // Again => 52 + 2 = 54.0.
        s.ProcessPrediction(Pred((7, 0.8)));
        Assert.AreEqual(54.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);

        // Negative channel 0.8 (index 6) => negStep = (0.8 - 0.3) * 4 = 2.0 => 54 - 2 = 52.0.
        s.ProcessPrediction(Pred((6, 0.8)));
        Assert.AreEqual(52.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
    }

    // ---------------------------------------------------------------- Upper clamp

    [TestMethod]
    public void ProcessPrediction_RepeatedStrongActivation_ClampsFingerAt100()
    {
        // Index (idx 2 = 1.0) each tick adds (1.0 - 0.3) * 4 = 0.7 * 4 = 2.8.
        // Unclamped after 40 ticks = 112.0, but Add clamps every result to [0, 100] => saturates at 100.0.
        var s = new StepwiseControlStrategy();

        for (var i = 0; i < 40; i++)
            s.ProcessPrediction(Pred((2, 1.0)));

        Assert.AreEqual(100.0, Value(s, DegreesOfActuation.Index), 1e-9);
    }

    // ---------------------------------------------------------------- Reset

    [TestMethod]
    public void ProcessPrediction_Reset_RestoresDefaults()
    {
        // Move a finger and a wrist axis off their defaults, then Reset() re-runs InitializeDefaults.
        var s = new StepwiseControlStrategy();
        s.ProcessPrediction(Pred((2, 0.8), (7, 0.8))); // Index -> 2.0, WristFlexionExtension -> 52.0

        s.Reset();

        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.Index), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
    }
}
