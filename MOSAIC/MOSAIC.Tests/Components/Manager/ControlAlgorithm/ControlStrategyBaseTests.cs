using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Manager.ControlAlgorithm;

namespace MOSAIC.Tests.Components.Manager.ControlAlgorithm;

/// <summary>
/// Known-answer tests for the protected mapping/clamp helpers on <see cref="ControlStrategyBase"/>.
/// The helpers are exercised through a tiny test-only subclass (<see cref="ExposedStrategy"/>) that
/// forwards each protected method to a public one. All arithmetic is derived by hand from the
/// definitions in the source and noted on each assertion.
/// </summary>
[TestClass]
public class ControlStrategyBaseTests
{
    /// <summary>
    /// Minimal concrete strategy that surfaces the protected helpers so they can be asserted directly.
    /// Defaults both DOAs used here to 0 (so Add starts from a known base).
    /// </summary>
    private sealed class ExposedStrategy : ControlStrategyBase
    {
        public override string Name => "Exposed";

        protected override void InitializeDefaults()
        {
            Set(DegreesOfActuation.Index, 0);
            Set(DegreesOfActuation.Middle, 0);
        }

        // ProcessPrediction is required by the base but is not the unit under test here.
        public override void ProcessPrediction(Vector<double> prediction) { }

        // --- protected helper pass-throughs ---
        public static double PublicGet(Vector<double> prediction, int index) => Get(prediction, index);
        public void PublicSet(DegreesOfActuation doa, double value) => Set(doa, value);
        public void PublicAdd(DegreesOfActuation doa, double delta) => Add(doa, delta);
        public void PublicMapDirect(DegreesOfActuation doa, Vector<double> p, int i) => MapDirect(doa, p, i);
        public void PublicMapOpposing(DegreesOfActuation doa, Vector<double> p, int pos, int neg)
            => MapOpposing(doa, p, pos, neg);
        public void PublicMapBipolar(DegreesOfActuation doa, Vector<double> p, int i, bool invert = false, double deadband = 0.0)
            => MapBipolar(doa, p, i, invert, deadband);
        public void PublicStepControl(DegreesOfActuation doa, double positive, double negative, double deadband, double stepSize)
            => StepControl(doa, positive, negative, deadband, stepSize);

        public double Value(DegreesOfActuation doa) => GetControlDict()[doa];
    }

    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    // ---------------------------------------------------------------- Get

    [TestMethod]
    public void Get_InBoundsIndex_ReturnsElement()
    {
        // prediction[1] = 0.42 is a valid index (0 <= 1 < 3) => returns the element unchanged.
        var p = Vec(0.1, 0.42, 0.9);

        var result = ExposedStrategy.PublicGet(p, 1);

        Assert.AreEqual(0.42, result, 1e-9);
    }

    [DataTestMethod]
    [DataRow(-1)]  // index < 0
    [DataRow(3)]   // index == Count
    [DataRow(99)]  // index > Count
    public void Get_OutOfBoundsIndex_ReturnsZero(int index)
    {
        // Guard: index < 0 or index >= prediction.Count => 0 (never throws).
        var p = Vec(1.0, 2.0, 3.0);

        var result = ExposedStrategy.PublicGet(p, index);

        Assert.AreEqual(0.0, result, 1e-9);
    }

    // ---------------------------------------------------------------- Set (clamping)

    [TestMethod]
    public void Set_ValueInsideRange_StoredUnchanged()
    {
        // Clamp(37, 0, 100) = 37 (already within range).
        var s = new ExposedStrategy();

        s.PublicSet(DegreesOfActuation.Index, 37.0);

        Assert.AreEqual(37.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void Set_ValueAboveMax_ClampsTo100()
    {
        // Clamp(150, 0, 100) = 100.
        var s = new ExposedStrategy();

        s.PublicSet(DegreesOfActuation.Index, 150.0);

        Assert.AreEqual(100.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void Set_ValueBelowMin_ClampsTo0()
    {
        // Clamp(-50, 0, 100) = 0.
        var s = new ExposedStrategy();

        s.PublicSet(DegreesOfActuation.Index, -50.0);

        Assert.AreEqual(0.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    // ---------------------------------------------------------------- Add (accumulate + clamp)

    [TestMethod]
    public void Add_FromDefault_AccumulatesWithinRange()
    {
        // Default Index = 0. Clamp(0 + 30, 0, 100) = 30.
        var s = new ExposedStrategy();

        s.PublicAdd(DegreesOfActuation.Index, 30.0);

        Assert.AreEqual(30.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void Add_OverflowingSum_ClampsTo100()
    {
        // 0 -> +70 = 70 -> +90 = 160 -> Clamp(160,0,100) = 100.
        var s = new ExposedStrategy();

        s.PublicAdd(DegreesOfActuation.Index, 70.0);
        s.PublicAdd(DegreesOfActuation.Index, 90.0);

        Assert.AreEqual(100.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void Add_NegativeDeltaBelowZero_ClampsTo0()
    {
        // 0 -> +40 = 40 -> -200 = -160 -> Clamp(-160,0,100) = 0.
        var s = new ExposedStrategy();

        s.PublicAdd(DegreesOfActuation.Index, 40.0);
        s.PublicAdd(DegreesOfActuation.Index, -200.0);

        Assert.AreEqual(0.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    // ---------------------------------------------------------------- MapDirect

    [TestMethod]
    public void MapDirect_Half_MapsTo50()
    {
        // Get(p,0) * 100 = 0.5 * 100 = 50.
        var s = new ExposedStrategy();
        var p = Vec(0.5);

        s.PublicMapDirect(DegreesOfActuation.Index, p, 0);

        Assert.AreEqual(50.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void MapDirect_AboveOne_ClampsTo100()
    {
        // 2.0 * 100 = 200 -> Set clamps to 100.
        var s = new ExposedStrategy();
        var p = Vec(2.0);

        s.PublicMapDirect(DegreesOfActuation.Index, p, 0);

        Assert.AreEqual(100.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    // ---------------------------------------------------------------- MapOpposing

    [TestMethod]
    public void MapOpposing_EqualInputs_MapsToNeutral50()
    {
        // (pos - neg + 1)/2 * 100 = (0 - 0 + 1)/2 * 100 = 50.
        var s = new ExposedStrategy();
        var p = Vec(0.0, 0.0);

        s.PublicMapOpposing(DegreesOfActuation.Index, p, 0, 1);

        Assert.AreEqual(50.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void MapOpposing_PositiveDominates_MapsTo100()
    {
        // (1 - 0 + 1)/2 * 100 = 100.
        var s = new ExposedStrategy();
        var p = Vec(1.0, 0.0);

        s.PublicMapOpposing(DegreesOfActuation.Index, p, 0, 1);

        Assert.AreEqual(100.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void MapOpposing_NegativeDominates_MapsTo0()
    {
        // (0 - 1 + 1)/2 * 100 = 0.
        var s = new ExposedStrategy();
        var p = Vec(0.0, 1.0);

        s.PublicMapOpposing(DegreesOfActuation.Index, p, 0, 1);

        Assert.AreEqual(0.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void MapOpposing_PartialImbalance_MapsToWeightedValue()
    {
        // pos=0.5, neg=0.3: (0.5 - 0.3 + 1)/2 * 100 = (1.2)/2 * 100 = 60.
        var s = new ExposedStrategy();
        var p = Vec(0.5, 0.3);

        s.PublicMapOpposing(DegreesOfActuation.Index, p, 0, 1);

        Assert.AreEqual(60.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    // ---------------------------------------------------------------- MapBipolar

    [TestMethod]
    public void MapBipolar_Zero_MapsToNeutral50()
    {
        // (0 + 1) * 50 = 50.
        var s = new ExposedStrategy();
        var p = Vec(0.0);

        s.PublicMapBipolar(DegreesOfActuation.Index, p, 0);

        Assert.AreEqual(50.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void MapBipolar_NegativeOne_MapsTo0()
    {
        // (-1 + 1) * 50 = 0.
        var s = new ExposedStrategy();
        var p = Vec(-1.0);

        s.PublicMapBipolar(DegreesOfActuation.Index, p, 0);

        Assert.AreEqual(0.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void MapBipolar_PositiveOne_MapsTo100()
    {
        // (1 + 1) * 50 = 100.
        var s = new ExposedStrategy();
        var p = Vec(1.0);

        s.PublicMapBipolar(DegreesOfActuation.Index, p, 0);

        Assert.AreEqual(100.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void MapBipolar_Half_MapsTo75()
    {
        // (0.5 + 1) * 50 = 75.
        var s = new ExposedStrategy();
        var p = Vec(0.5);

        s.PublicMapBipolar(DegreesOfActuation.Index, p, 0);

        Assert.AreEqual(75.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void MapBipolar_Invert_FlipsSignBeforeMapping()
    {
        // invert => bipolar = -(0.5) = -0.5; (-0.5 + 1) * 50 = 25.
        var s = new ExposedStrategy();
        var p = Vec(0.5);

        s.PublicMapBipolar(DegreesOfActuation.Index, p, 0, invert: true);

        Assert.AreEqual(25.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void MapBipolar_WithinDeadband_SnapsToNeutral50()
    {
        // |0.05| < 0.1 => bipolar forced to 0 => (0 + 1) * 50 = 50.
        var s = new ExposedStrategy();
        var p = Vec(0.05);

        s.PublicMapBipolar(DegreesOfActuation.Index, p, 0, deadband: 0.1);

        Assert.AreEqual(50.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void MapBipolar_AtDeadbandBoundary_NotSuppressed()
    {
        // Boundary is exclusive: |0.1| < 0.1 is false => value passes => (0.1 + 1) * 50 = 55.
        var s = new ExposedStrategy();
        var p = Vec(0.1);

        s.PublicMapBipolar(DegreesOfActuation.Index, p, 0, deadband: 0.1);

        Assert.AreEqual(55.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void MapBipolar_BeyondUnitRange_ClampsTo100()
    {
        // (2.0 + 1) * 50 = 150 -> Math.Clamp(...,0,100) = 100.
        var s = new ExposedStrategy();
        var p = Vec(2.0);

        s.PublicMapBipolar(DegreesOfActuation.Index, p, 0);

        Assert.AreEqual(100.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    // ---------------------------------------------------------------- StepControl

    [TestMethod]
    public void StepControl_PositiveAboveDeadband_AddsScaledStep()
    {
        // positive=0.5 > deadband=0.1 => posStep = max(0, 0.5-0.1)*10 = 4, negStep = 0.
        // Add(0.5*... ) from default 0 => Clamp(0 + 4) = 4.
        var s = new ExposedStrategy();

        s.PublicStepControl(DegreesOfActuation.Index, positive: 0.5, negative: 0.0, deadband: 0.1, stepSize: 10.0);

        Assert.AreEqual(4.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void StepControl_NegativeAboveDeadband_SubtractsScaledStep()
    {
        // First raise Index to 50. Then negative=0.6 > deadband=0.1:
        // posStep=0, negStep = max(0, 0.6-0.1)*10 = 5 => Add(0 - 5) => Clamp(50 - 5) = 45.
        var s = new ExposedStrategy();
        s.PublicSet(DegreesOfActuation.Index, 50.0);

        s.PublicStepControl(DegreesOfActuation.Index, positive: 0.0, negative: 0.6, deadband: 0.1, stepSize: 10.0);

        Assert.AreEqual(45.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void StepControl_BothBelowDeadband_LeavesValueUnchanged()
    {
        // Neither 0.05 nor 0.05 exceeds deadband 0.1 => guard fails => no Add => value stays 50.
        var s = new ExposedStrategy();
        s.PublicSet(DegreesOfActuation.Index, 50.0);

        s.PublicStepControl(DegreesOfActuation.Index, positive: 0.05, negative: 0.05, deadband: 0.1, stepSize: 10.0);

        Assert.AreEqual(50.0, s.Value(DegreesOfActuation.Index), 1e-9);
    }

    // ---------------------------------------------------------------- Reset / GetControlDict

    [TestMethod]
    public void Reset_AfterMutations_RestoresDefaults()
    {
        // Defaults set both DOAs to 0. After mutating, Reset() re-runs InitializeDefaults => back to 0.
        var s = new ExposedStrategy();
        s.PublicSet(DegreesOfActuation.Index, 80.0);
        s.PublicSet(DegreesOfActuation.Middle, 20.0);

        s.Reset();

        Assert.AreEqual(0.0, s.Value(DegreesOfActuation.Index), 1e-9);
        Assert.AreEqual(0.0, s.Value(DegreesOfActuation.Middle), 1e-9);
    }

    [TestMethod]
    public void GetControlDict_AfterInit_ContainsAllDefaultKeys()
    {
        // InitializeDefaults populates exactly the two DOAs => both keys present after construction.
        var s = new ExposedStrategy();

        var dict = s.GetControlDict();

        Assert.AreEqual(2, dict.Count);
        Assert.IsTrue(dict.ContainsKey(DegreesOfActuation.Index));
        Assert.IsTrue(dict.ContainsKey(DegreesOfActuation.Middle));
    }
}
