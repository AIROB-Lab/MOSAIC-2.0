using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Enums;

namespace MOSAIC.Components.Manager.ControlAlgorithm;

/// <summary>
/// Stepwise control - incremental changes with deadband threshold.
/// Values change gradually rather than jumping directly to target.
/// </summary>
public class StepwiseControlStrategy : ControlStrategyBase
{
    public override string Name => "StepwiseControl";

    private const double StepSize = 100 / 25.0;  // Max step per tick
    private const double Deadband = 0.3;         // Activation threshold

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
        if (prediction.Count == 0) return;

        var open = Get(prediction, 12);  // Open hand signal

        StepControl(DegreesOfActuation.ThumbFlexion, Get(prediction, 0), open, Deadband, StepSize);
        StepControl(DegreesOfActuation.ThumbRotation, Get(prediction, 1), open, Deadband, StepSize);
        StepControl(DegreesOfActuation.Index, Get(prediction, 2), open, Deadband, StepSize);
        StepControl(DegreesOfActuation.Middle, Get(prediction, 3), open, Deadband, StepSize);
        StepControl(DegreesOfActuation.Ring, Get(prediction, 4), open, Deadband, StepSize);
        StepControl(DegreesOfActuation.Little, Get(prediction, 5), open, Deadband, StepSize);

        StepControl(DegreesOfActuation.WristFlexionExtension, Get(prediction, 7), Get(prediction, 6), Deadband, StepSize);
        StepControl(DegreesOfActuation.WristPronationSupination, Get(prediction, 9), Get(prediction, 8), Deadband, StepSize);
        StepControl(DegreesOfActuation.WristUlnarRadial, Get(prediction, 10), Get(prediction, 11), Deadband, StepSize);

        StepControl(DegreesOfActuation.ElbowFlexion, Get(prediction, 13), open, Deadband, StepSize);
        StepControl(DegreesOfActuation.ElbowExtension, Get(prediction, 14), open, Deadband, StepSize);
    }
}