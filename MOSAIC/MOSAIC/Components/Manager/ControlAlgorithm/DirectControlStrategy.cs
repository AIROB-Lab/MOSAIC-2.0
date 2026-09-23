using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Enums;

namespace MOSAIC.Components.Manager.ControlAlgorithm;

/// <summary>
/// Direct control - maps prediction values directly to DOA actuation [0,100].
/// </summary>
public class DirectControlStrategy : ControlStrategyBase
{
    public override string Name => "DirectControl";

    protected override void InitializeDefaults()
    {
        Set(DegreesOfActuation.ThumbFlexion, 0);
        Set(DegreesOfActuation.ThumbRotation, 0);
        Set(DegreesOfActuation.Index, 0);
        Set(DegreesOfActuation.Middle, 0);
        Set(DegreesOfActuation.Ring, 0);
        Set(DegreesOfActuation.Little, 0);
        Set(DegreesOfActuation.WristFlexionExtension, 50);
        Set(DegreesOfActuation.WristPronationSupination, 50);
        Set(DegreesOfActuation.WristUlnarRadial, 50);
        Set(DegreesOfActuation.ElbowFlexion, 0);
        Set(DegreesOfActuation.ElbowExtension, 0);
    }

    public override void ProcessPrediction(Vector<double> prediction)
    {
        if (prediction == null || prediction.Count == 0) return;

        MapDirect(DegreesOfActuation.ThumbFlexion, prediction, 0);
        MapDirect(DegreesOfActuation.ThumbRotation, prediction, 1);
        MapDirect(DegreesOfActuation.Index, prediction, 2);
        MapDirect(DegreesOfActuation.Middle, prediction, 3);
        MapDirect(DegreesOfActuation.Ring, prediction, 4);
        MapDirect(DegreesOfActuation.Little, prediction, 5);

        MapOpposing(DegreesOfActuation.WristFlexionExtension, prediction, 7, 6);      // Ext vs Flex
        MapOpposing(DegreesOfActuation.WristPronationSupination, prediction, 8, 9);   // Pron vs Sup
        MapOpposing(DegreesOfActuation.WristUlnarRadial, prediction, 10, 11);         // Ulnar vs Radial

        MapDirect(DegreesOfActuation.ElbowFlexion, prediction, 13);
        MapDirect(DegreesOfActuation.ElbowExtension, prediction, 14);
    }
}