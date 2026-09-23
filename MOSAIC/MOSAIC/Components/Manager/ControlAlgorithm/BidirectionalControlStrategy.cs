using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Enums;

namespace MOSAIC.Components.Manager.ControlAlgorithm;

/// <summary>
/// Bidirectional direct control — all DOAs centered at 50 (rest).
/// Fingers use opposing pairs: close (per-finger) vs open (shared index [12]).
/// Range: 0 = full close/fist, 50 = rest, 100 = overstretch/extension.
/// </summary>
/// <remarks>
/// <para><strong>Prediction vector layout:</strong></para>
/// <code>
/// [0]  th_flex       [6]  wr_flex     [12] hand_open
/// [1]  th_rot        [7]  wr_ext      [13] elbow_flex
/// [2]  index         [8]  wr_pron     [14] elbow_ext
/// [3]  middle        [9]  wr_sup
/// [4]  ring          [10] wr_ulnar
/// [5]  little        [11] wr_radial
/// </code>
/// </remarks>
public class BidirectionalControlStrategy : ControlStrategyBase
{
    public override string Name => "BidirectionalControl";

    /// <summary>Rest position for finger DOAs (0 = fist, 50 = relaxed, 100 = overstretch).</summary>
    private const double FingerRest = 50;

    /// <summary>Rest position for wrist/elbow DOAs (0 = full one direction, 50 = neutral, 100 = full other).</summary>
    private const double WristElbowRest = 50;

    protected override void InitializeDefaults()
    {
        Set(DegreesOfActuation.ThumbFlexion, FingerRest);
        Set(DegreesOfActuation.ThumbRotation, FingerRest);
        Set(DegreesOfActuation.Index, FingerRest);
        Set(DegreesOfActuation.Middle, FingerRest);
        Set(DegreesOfActuation.Ring, FingerRest);
        Set(DegreesOfActuation.Little, FingerRest);
        Set(DegreesOfActuation.WristFlexionExtension, WristElbowRest);
        Set(DegreesOfActuation.WristPronationSupination, WristElbowRest);
        Set(DegreesOfActuation.WristUlnarRadial, WristElbowRest);
        Set(DegreesOfActuation.ElbowFlexion, WristElbowRest);
        Set(DegreesOfActuation.ElbowExtension, WristElbowRest);
    }

    public override void ProcessPrediction(Vector<double> prediction)
    {
        if (prediction == null || prediction.Count == 0) return;

        // Fingers — asymmetric opposing: close drives toward 0, open drives toward 100
        // Rest at 50: close range [0..50], open range [50..100]
        MapOpposingAsymmetric(DegreesOfActuation.ThumbFlexion, prediction, 0, 12, FingerRest);
        MapOpposingAsymmetric(DegreesOfActuation.ThumbRotation, prediction, 1, 12, FingerRest);
        MapOpposingAsymmetric(DegreesOfActuation.Index, prediction, 2, 12, FingerRest);
        MapOpposingAsymmetric(DegreesOfActuation.Middle, prediction, 3, 12, FingerRest);
        MapOpposingAsymmetric(DegreesOfActuation.Ring, prediction, 4, 12, FingerRest);
        MapOpposingAsymmetric(DegreesOfActuation.Little, prediction, 5, 12, FingerRest);

        // Wrist — symmetric opposing pairs (centered at 50)
        MapOpposing(DegreesOfActuation.WristFlexionExtension, prediction, 7, 6);
        MapOpposing(DegreesOfActuation.WristPronationSupination, prediction, 8, 9);
        MapOpposing(DegreesOfActuation.WristUlnarRadial, prediction, 10, 11);

        // Elbow — symmetric opposing pair
        MapOpposing(DegreesOfActuation.ElbowFlexion, prediction, 13, 14);
    }

    /// <summary>
    /// Maps two opposing predictions to a single DOA with an asymmetric rest point.
    /// Predictions are in [0..1]. The close prediction drives from rest toward 0 (fist),
    /// the open prediction drives from rest toward 100 (overstretch).
    /// </summary>
    /// <param name="doa">Target degree of actuation.</param>
    /// <param name="prediction">Full prediction vector.</param>
    /// <param name="closeIdx">Index of the "close/fist" prediction.</param>
    /// <param name="openIdx">Index of the "open/overstretch" prediction.</param>
    /// <param name="rest">Rest point value (e.g. 50 for fingers).</param>
    private void MapOpposingAsymmetric(DegreesOfActuation doa, Vector<double> prediction,
                                        int closeIdx, int openIdx, double rest)
    {
        if (closeIdx >= prediction.Count || openIdx >= prediction.Count) return;

        double close = System.Math.Clamp(prediction[closeIdx], 0, 1);
        double open  = System.Math.Clamp(prediction[openIdx], 0, 1);

        // Net activation: positive = opening, negative = closing
        double net = open - close;

        double value;
        if (net >= 0)
        {
            // Opening: rest → 100
            value = rest + net * (100 - rest);
        }
        else
        {
            // Closing: rest → 0
            value = rest + net * rest;
        }

        Set(doa, System.Math.Clamp(value, 0, 100));
    }
}