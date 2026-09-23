using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Enums;
using MOSAIC.Components.Manager.ControlAlgorithm;

namespace MOSAIC.Tests.Components.Manager.ControlAlgorithm;

/// <summary>
/// Known-answer tests for <see cref="BidirectionalControlStrategy"/>.
///
/// <para>Prediction layout (indices into the vector):</para>
/// <code>
/// [0] th_flex  [1] th_rot  [2] index  [3] middle  [4] ring  [5] little
/// [6] wr_flex  [7] wr_ext  [8] wr_pron [9] wr_sup [10] wr_ulnar [11] wr_radial
/// [12] hand_open  [13] elbow_flex  [14] elbow_ext
/// </code>
///
/// <para>Fingers use <c>MapOpposingAsymmetric(doa, p, closeIdx, openIdx, rest=FingerRest)</c>.
/// The source constant <c>FingerRest = 50</c> (the "Rest at 40" doc comment is stale — the code
/// uses 50). With rest = 50, <em>both</em> branches of the helper reduce to the same line, so:</para>
/// <code>
///   close = Clamp(p[closeIdx], 0, 1);  open = Clamp(p[openIdx], 0, 1);  net = open - close;
///   value = 50 + net * 50            // (net &gt;= 0: 50 + net*(100-50); net &lt; 0: 50 + net*50)
///   doa   = Clamp(value, 0, 100);
/// </code>
/// <para>The single "open" channel is <c>hand_open = p[12]</c>, shared by every finger.</para>
///
/// <para>Wrist and elbow use the base <c>MapOpposing(doa, p, pos, neg)</c>:
/// <c>value = Clamp((pos - neg + 1) / 2 * 100, 0, 100)</c>, where pos/neg are the raw
/// (un-clamped) vector elements. Mapping: WristFlexExt(pos=7,neg=6), WristPronSup(pos=8,neg=9),
/// WristUlnarRadial(pos=10,neg=11), ElbowFlexion(pos=13,neg=14). ElbowExtension is never mapped —
/// it stays at its rest default.</para>
///
/// All arithmetic below is derived by hand from those definitions and noted on each assertion.
/// </summary>
[TestClass]
public class BidirectionalControlStrategyTests
{
    /// <summary>Builds a 15-element prediction (all zero) with the given indices overridden.</summary>
    private static Vector<double> Prediction(params (int idx, double val)[] entries)
    {
        var v = Vector<double>.Build.Dense(15, 0.0);
        foreach (var (idx, val) in entries) v[idx] = val;
        return v;
    }

    private static double Value(BidirectionalControlStrategy s, DegreesOfActuation doa)
        => s.GetControlDict()[doa];

    // ---------------------------------------------------------------- Identity / defaults

    [TestMethod]
    public void Name_ReturnsBidirectionalControl()
    {
        // The strategy identifies itself with a fixed name used for JSON export / UI.
        var s = new BidirectionalControlStrategy();

        Assert.AreEqual("BidirectionalControl", s.Name);
    }

    [TestMethod]
    public void Constructor_InitializesElevenDoasAtRest50()
    {
        // InitializeDefaults sets exactly 11 DOAs (6 fingers, 3 wrist, ElbowFlexion, ElbowExtension),
        // all to 50 (FingerRest = WristElbowRest = 50). HandOpenClose is NOT a control output.
        var s = new BidirectionalControlStrategy();

        var dict = s.GetControlDict();

        Assert.AreEqual(11, dict.Count);
        Assert.IsFalse(dict.ContainsKey(DegreesOfActuation.HandOpenClose));
        Assert.AreEqual(50.0, dict[DegreesOfActuation.ThumbFlexion], 1e-9);
        Assert.AreEqual(50.0, dict[DegreesOfActuation.Index], 1e-9);
        Assert.AreEqual(50.0, dict[DegreesOfActuation.WristFlexionExtension], 1e-9);
        Assert.AreEqual(50.0, dict[DegreesOfActuation.ElbowFlexion], 1e-9);
        Assert.AreEqual(50.0, dict[DegreesOfActuation.ElbowExtension], 1e-9);
    }

    // ---------------------------------------------------------------- Guard clause

    [TestMethod]
    public void ProcessPrediction_EmptyVector_LeavesDefaultsUntouched()
    {
        // Guard: prediction.Count == 0 => early return, nothing mutated => still all 50.
        var s = new BidirectionalControlStrategy();

        s.ProcessPrediction(Vector<double>.Build.Dense(0));

        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.Index), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
    }

    // ---------------------------------------------------------------- All-neutral input

    [TestMethod]
    public void ProcessPrediction_AllZeros_HoldsEveryDoaAtRest50()
    {
        // Fingers: net = 0 - 0 = 0 => 50 + 0 = 50.
        // Wrist/elbow: (0 - 0 + 1)/2 * 100 = 50.
        var s = new BidirectionalControlStrategy();

        s.ProcessPrediction(Prediction());

        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.ThumbFlexion), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.Little), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristPronationSupination), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristUlnarRadial), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.ElbowFlexion), 1e-9);
    }

    // ---------------------------------------------------------------- Finger mapping (asymmetric, rest 50)

    [DataTestMethod]
    // (closeVal, openVal, expected)  value = 50 + (Clamp(open,0,1) - Clamp(close,0,1)) * 50
    [DataRow(0.0, 0.0, 50.0)]   // net 0        => 50
    [DataRow(1.0, 0.0, 0.0)]    // net -1       => 50 - 50 = 0  (full close/fist)
    [DataRow(0.0, 1.0, 100.0)]  // net +1       => 50 + 50 = 100 (full open/overstretch)
    [DataRow(0.5, 0.0, 25.0)]   // net -0.5     => 50 - 25 = 25
    [DataRow(0.0, 0.5, 75.0)]   // net +0.5     => 50 + 25 = 75
    [DataRow(0.4, 0.6, 60.0)]   // net +0.2     => 50 + 10 = 60 (opening branch)
    [DataRow(0.6, 0.4, 40.0)]   // net -0.2     => 50 - 10 = 40 (closing branch)
    [DataRow(1.0, 1.0, 50.0)]   // net 0        => 50 (both maxed)
    [DataRow(2.0, 0.0, 0.0)]    // close clamps to 1 => net -1 => 0
    [DataRow(0.0, 5.0, 100.0)]  // open clamps to 1  => net +1 => 100
    public void ProcessPrediction_IndexFinger_MapsClosePlusSharedOpen(double closeVal, double openVal, double expected)
    {
        // Drive the index finger's close channel (idx 2) and the shared hand_open channel (idx 12).
        var s = new BidirectionalControlStrategy();

        s.ProcessPrediction(Prediction((2, closeVal), (12, openVal)));

        Assert.AreEqual(expected, Value(s, DegreesOfActuation.Index), 1e-9);
    }

    [TestMethod]
    public void ProcessPrediction_HandOpenChannel_DrivesAllSixFingersToOverstretch()
    {
        // hand_open (idx 12) is shared by every finger. With all close channels 0 and open = 1,
        // each finger has net = 1 - 0 = 1 => 50 + 50 = 100.
        var s = new BidirectionalControlStrategy();

        s.ProcessPrediction(Prediction((12, 1.0)));

        Assert.AreEqual(100.0, Value(s, DegreesOfActuation.ThumbFlexion), 1e-9);
        Assert.AreEqual(100.0, Value(s, DegreesOfActuation.ThumbRotation), 1e-9);
        Assert.AreEqual(100.0, Value(s, DegreesOfActuation.Index), 1e-9);
        Assert.AreEqual(100.0, Value(s, DegreesOfActuation.Middle), 1e-9);
        Assert.AreEqual(100.0, Value(s, DegreesOfActuation.Ring), 1e-9);
        Assert.AreEqual(100.0, Value(s, DegreesOfActuation.Little), 1e-9);
    }

    [TestMethod]
    public void ProcessPrediction_PerFingerCloseChannels_MapIndependently()
    {
        // With hand_open = 0, each finger value = 50 - Clamp(close,0,1) * 50, using its own close index:
        //   ThumbFlexion(idx0)=0.2 => 50 - 10 = 40;  Middle(idx3)=1.0 => 0;  Ring(idx4)=0.0 => 50.
        var s = new BidirectionalControlStrategy();

        s.ProcessPrediction(Prediction((0, 0.2), (3, 1.0), (4, 0.0)));

        Assert.AreEqual(40.0, Value(s, DegreesOfActuation.ThumbFlexion), 1e-9);
        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.Middle), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.Ring), 1e-9);
    }

    // ---------------------------------------------------------------- Wrist mapping (symmetric opposing)

    [DataTestMethod]
    // (extVal, flexVal, expected)  value = Clamp((ext - flex + 1)/2 * 100, 0, 100); pos=idx7, neg=idx6
    [DataRow(0.0, 0.0, 50.0)]   // (0 - 0 + 1)/2 * 100 = 50
    [DataRow(1.0, 0.0, 100.0)]  // (1 - 0 + 1)/2 * 100 = 100 (extension)
    [DataRow(0.0, 1.0, 0.0)]    // (0 - 1 + 1)/2 * 100 = 0   (flexion)
    [DataRow(0.5, 0.3, 60.0)]   // (0.5 - 0.3 + 1)/2 * 100 = 1.2/2*100 = 60
    [DataRow(0.3, 0.5, 40.0)]   // (0.3 - 0.5 + 1)/2 * 100 = 0.8/2*100 = 40
    [DataRow(2.0, 0.0, 100.0)]  // (2 - 0 + 1)/2 * 100 = 150 -> Set clamps to 100
    [DataRow(0.0, 2.0, 0.0)]    // (0 - 2 + 1)/2 * 100 = -50 -> Set clamps to 0
    public void ProcessPrediction_WristFlexionExtension_MapsOpposingPair(double extVal, double flexVal, double expected)
    {
        // idx7 = wr_ext (positive), idx6 = wr_flex (negative).
        var s = new BidirectionalControlStrategy();

        s.ProcessPrediction(Prediction((7, extVal), (6, flexVal)));

        Assert.AreEqual(expected, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
    }

    [TestMethod]
    public void ProcessPrediction_WristPronSupAndUlnarRadial_MapTheirOwnPairs()
    {
        // PronSup: pos=idx8(pron), neg=idx9(sup): (0.8 - 0.2 + 1)/2 * 100 = 1.6/2*100 = 80.
        // UlnarRadial: pos=idx10(ulnar), neg=idx11(radial): (0.0 - 1.0 + 1)/2 * 100 = 0.
        var s = new BidirectionalControlStrategy();

        s.ProcessPrediction(Prediction((8, 0.8), (9, 0.2), (10, 0.0), (11, 1.0)));

        Assert.AreEqual(80.0, Value(s, DegreesOfActuation.WristPronationSupination), 1e-9);
        Assert.AreEqual(0.0, Value(s, DegreesOfActuation.WristUlnarRadial), 1e-9);
    }

    // ---------------------------------------------------------------- Elbow mapping

    [TestMethod]
    public void ProcessPrediction_ElbowFlexionMapped_ExtensionStaysAtRest()
    {
        // ElbowFlexion: pos=idx13(flex), neg=idx14(ext): (1 - 0 + 1)/2 * 100 = 100.
        // ElbowExtension is never mapped by ProcessPrediction, so it keeps its rest default of 50.
        var s = new BidirectionalControlStrategy();

        s.ProcessPrediction(Prediction((13, 1.0), (14, 0.0)));

        Assert.AreEqual(100.0, Value(s, DegreesOfActuation.ElbowFlexion), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.ElbowExtension), 1e-9);
    }

    // ---------------------------------------------------------------- Short-vector guard (no throw)

    [TestMethod]
    public void ProcessPrediction_ShortVector_DoesNotThrowAndHoldsRest()
    {
        // Count = 6 (indices 0..5 valid). Finger helper guards on openIdx(12) >= Count => early return,
        // so all fingers keep their 50 default. Wrist/elbow indices (6..14) are out of bounds; base Get
        // returns 0, so each opposing map yields (0 - 0 + 1)/2 * 100 = 50. Nothing throws.
        var s = new BidirectionalControlStrategy();

        s.ProcessPrediction(Vector<double>.Build.Dense(6, 0.9));

        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.Index), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.Little), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.ElbowFlexion), 1e-9);
    }

    // ---------------------------------------------------------------- Reset

    [TestMethod]
    public void Reset_AfterProcessPrediction_RestoresRestDefaults()
    {
        // Drive several DOAs away from rest, then Reset() re-runs InitializeDefaults => all back to 50.
        var s = new BidirectionalControlStrategy();
        s.ProcessPrediction(Prediction((2, 1.0), (12, 0.0), (7, 1.0), (13, 1.0)));

        s.Reset();

        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.Index), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.WristFlexionExtension), 1e-9);
        Assert.AreEqual(50.0, Value(s, DegreesOfActuation.ElbowFlexion), 1e-9);
    }
}
