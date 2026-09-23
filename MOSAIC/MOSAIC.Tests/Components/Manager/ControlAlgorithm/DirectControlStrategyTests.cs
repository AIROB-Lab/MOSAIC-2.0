using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Manager.ControlAlgorithm;

namespace MOSAIC.Tests.Components.Manager.ControlAlgorithm;

/// <summary>
/// Known-answer tests for <see cref="DirectControlStrategy"/>, a concrete
/// <see cref="ControlStrategyBase"/> that maps a prediction vector straight onto the
/// DOA actuation values. Every expected value is derived by hand from the mapping in
/// <c>ProcessPrediction</c> together with the base helpers:
///   MapDirect(doa, p, i)          => Set(doa, Get(p, i) * 100)
///   MapOpposing(doa, p, pos, neg) => Set(doa, (Get(p,pos) - Get(p,neg) + 1) / 2 * 100)
///   Set(doa, v)                   => ControlDict[doa] = Clamp(v, 0, 100)
///   Get(p, i)                     => (0 &lt;= i &lt; p.Count) ? p[i] : 0
/// Index mapping used by DirectControlStrategy:
///   ThumbFlexion=p0, ThumbRotation=p1, Index=p2, Middle=p3, Ring=p4, Little=p5,
///   WristFlexionExtension=MapOpposing(pos:7, neg:6),
///   WristPronationSupination=MapOpposing(pos:8, neg:9),
///   WristUlnarRadial=MapOpposing(pos:10, neg:11),
///   ElbowFlexion=p13, ElbowExtension=p14   (p12 is unused).
/// </summary>
[TestClass]
public class DirectControlStrategyTests
{
    private static Vector<double> Vec(params double[] values) => Vector<double>.Build.Dense(values);

    private static double Value(DirectControlStrategy s, DegreesOfActuation doa) => s.GetControlDict()[doa];

    // ---------------------------------------------------------------- identity / defaults

    [TestMethod]
    public void Name_ReturnsDirectControl()
    {
        // Name is a fixed literal on the strategy.
        var s = new DirectControlStrategy();

        Assert.AreEqual("DirectControl", s.Name);
    }

    [TestMethod]
    public void Constructor_InitializesDefaults()
    {
        // InitializeDefaults sets exactly 11 DOAs: six fingers + three wrist axes + two elbow.
        // Fingers and elbow default to 0; the three wrist axes default to 50 (neutral).
        var s = new DirectControlStrategy();

        var dict = s.GetControlDict();

        Assert.AreEqual(11, dict.Count);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.ThumbFlexion], 1e-9);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.ThumbRotation], 1e-9);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.Index], 1e-9);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.Middle], 1e-9);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.Ring], 1e-9);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.Little], 1e-9);
        Assert.AreEqual(50.0, dict[DegreesOfActuation.WristFlexionExtension], 1e-9);
        Assert.AreEqual(50.0, dict[DegreesOfActuation.WristPronationSupination], 1e-9);
        Assert.AreEqual(50.0, dict[DegreesOfActuation.WristUlnarRadial], 1e-9);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.ElbowFlexion], 1e-9);
        Assert.AreEqual(0.0, dict[DegreesOfActuation.ElbowExtension], 1e-9);
        // HandOpenClose is never populated by this strategy.
        Assert.IsFalse(dict.ContainsKey(DegreesOfActuation.HandOpenClose));
    }

    // ---------------------------------------------------------------- full known-answer mapping

    [TestMethod]
    public void ProcessPrediction_FullVector_MapsEachDoa()
    {
        // p = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.3, 0.5, 0.2, 0.4, 0.7, 0.2, 0.0, 0.9, 0.1]
        //      idx  0    1    2    3    4    5    6    7    8    9   10   11   12   13   14
        var s = new DirectControlStrategy();
        var p = Vec(0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.3, 0.5, 0.2, 0.4, 0.7, 0.2, 0.0, 0.9, 0.1);

        s.ProcessPrediction(p);

        // Fingers: MapDirect => p[i] * 100
        Assert.AreEqual(10.0, Value(s, DegreesOfActuation.ThumbFlexion), 1e-9);   // 0.1 * 100
        Assert.AreEqual(20.0, Value(s, DegreesOfActuation.ThumbRotation), 1e-9);  // 0.2 * 100
        Assert.AreEqual(30.0, Value(s, DegreesOfActuation.Index), 1e-9);          // 0.3 * 100
        Assert.AreEqual(40.0, Value(s, DegreesOfActuation.Middle), 1e-9);         // 0.4 * 100
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.Ring), 1e-9);           // 0.5 * 100
        Assert.AreEqual(60.0, Value(s, DegreesOfActuation.Little), 1e-9);         // 0.6 * 100

        // Wrist: MapOpposing => (pos - neg + 1) / 2 * 100
        // FlexExt  pos=p[7]=0.5, neg=p[6]=0.3 => (0.5 - 0.3 + 1)/2 * 100 = 60
        Assert.AreEqual(60.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
        // PronSup  pos=p[8]=0.2, neg=p[9]=0.4 => (0.2 - 0.4 + 1)/2 * 100 = 40
        Assert.AreEqual(40.0, Value(s, DegreesOfActuation.WristPronationSupination), 1e-9);
        // UlnarRad pos=p[10]=0.7, neg=p[11]=0.2 => (0.7 - 0.2 + 1)/2 * 100 = 75
        Assert.AreEqual(75.0, Value(s, DegreesOfActuation.WristUlnarRadial), 1e-9);

        // Elbow: MapDirect => p[i] * 100
        Assert.AreEqual(90.0, Value(s, DegreesOfActuation.ElbowFlexion), 1e-9);   // 0.9 * 100
        Assert.AreEqual(10.0, Value(s, DegreesOfActuation.ElbowExtension), 1e-9); // 0.1 * 100
    }

    // ---------------------------------------------------------------- clamping

    [TestMethod]
    public void ProcessPrediction_ValuesAboveOne_ClampTo100()
    {
        // Index maps from p[2]. 2.0 * 100 = 200 -> Set clamps to 100.
        var s = new DirectControlStrategy();
        var p = Vec(0.0, 0.0, 2.0);

        s.ProcessPrediction(p);

        Assert.AreEqual(100.0, Value(s, DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void ProcessPrediction_NegativeValues_ClampTo0()
    {
        // ThumbFlexion maps from p[0]. -0.5 * 100 = -50 -> Set clamps to 0.
        var s = new DirectControlStrategy();
        var p = Vec(-0.5);

        s.ProcessPrediction(p);

        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.ThumbFlexion), 1e-9);
    }

    // ---------------------------------------------------------------- wrist opposing extremes

    [TestMethod]
    public void ProcessPrediction_WristPositiveDominates_MapsTo100()
    {
        // WristFlexionExtension uses pos=p[7], neg=p[6]: (1 - 0 + 1)/2 * 100 = 100.
        var s = new DirectControlStrategy();
        var p = Vec(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0);

        s.ProcessPrediction(p);

        Assert.AreEqual(100.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
    }

    [TestMethod]
    public void ProcessPrediction_WristNegativeDominates_MapsTo0()
    {
        // WristFlexionExtension uses pos=p[7]=0, neg=p[6]=1: (0 - 1 + 1)/2 * 100 = 0.
        var s = new DirectControlStrategy();
        var p = Vec(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0);

        s.ProcessPrediction(p);

        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
    }

    // ---------------------------------------------------------------- short vector (out-of-bounds -> 0)

    [TestMethod]
    public void ProcessPrediction_ShortVector_UsesZeroForMissingIndicesAndNeutralWrist()
    {
        // Only indices 0,1,2 present; every Get beyond that returns 0.
        var s = new DirectControlStrategy();
        var p = Vec(0.5, 0.5, 0.5);

        s.ProcessPrediction(p);

        // Present fingers map directly.
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.ThumbFlexion), 1e-9);   // 0.5 * 100
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.ThumbRotation), 1e-9);  // 0.5 * 100
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.Index), 1e-9);          // 0.5 * 100
        // Missing fingers -> Get returns 0 -> 0.
        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.Middle), 1e-9);
        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.Ring), 1e-9);
        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.Little), 1e-9);
        // Wrist opposing pairs both read 0 => (0 - 0 + 1)/2 * 100 = 50 (neutral).
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristPronationSupination), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristUlnarRadial), 1e-9);
        // Elbow indices absent -> 0.
        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.ElbowFlexion), 1e-9);
        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.ElbowExtension), 1e-9);
    }

    // ---------------------------------------------------------------- guards

    [TestMethod]
    public void ProcessPrediction_EmptyVector_LeavesDefaultsUnchanged()
    {
        // Count == 0 => early return, so the defaults from construction remain.
        var s = new DirectControlStrategy();

        s.ProcessPrediction(Vector<double>.Build.Dense(0));

        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.Index), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
    }

    [TestMethod]
    public void ProcessPrediction_Null_LeavesDefaultsUnchanged()
    {
        // prediction == null => early return; defaults remain intact.
        var s = new DirectControlStrategy();

        s.ProcessPrediction(null!);

        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.Index), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristPronationSupination), 1e-9);
    }

    // ---------------------------------------------------------------- reset

    [TestMethod]
    public void Reset_AfterProcess_RestoresDefaults()
    {
        // Drive non-default values, then Reset() re-runs InitializeDefaults.
        var s = new DirectControlStrategy();
        s.ProcessPrediction(Vec(1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0, 0.0, 0.0, 1.0, 1.0));

        s.Reset();

        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.Index), 1e-9);                    // finger back to 0
        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.ElbowFlexion), 1e-9);             // elbow back to 0
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);   // wrist back to 50
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristUlnarRadial), 1e-9);        // wrist back to 50
    }
}
