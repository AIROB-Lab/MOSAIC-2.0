using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Enums;

namespace MOSAIC.Components.Manager.ControlAlgorithm;

/// <summary>
/// Maps a deep-learning regression vector (8 outputs) to prosthetic Degrees of Actuation.
/// </summary>
/// <remarks>
/// The prediction vector must follow this public interface contract:
///
/// <list type="table">
///   <listheader><term>Index</term><description>Output</description><description>Range</description></listheader>
///   <item><term>0</term><description>Index finger</description>           <description>[0, 1]</description></item>
///   <item><term>1</term><description>Middle finger</description>          <description>[0, 1]</description></item>
///   <item><term>2</term><description>Ring finger</description>            <description>[0, 1]</description></item>
///   <item><term>3</term><description>Little finger</description>          <description>[0, 1]</description></item>
///   <item><term>4</term><description>Thumb flexion</description>          <description>[0, 1]</description></item>
///   <item><term>5</term><description>Wrist flex(+) / ext(-)</description>    <description>[-1, 1]</description></item>
///   <item><term>6</term><description>Wrist pron(+) / sup(-)</description>    <description>[-1, 1]</description></item>
///   <item><term>7</term><description>Wrist ulnar(+) / radial(-)</description><description>[-1, 1]</description></item>
/// </list>
///
/// Finger values are scaled from [0,1] to [0,100] via <see cref="ControlStrategyBase.MapDirect"/>.
/// Wrist values are scaled from [-1,+1] to [0,100] via <see cref="ControlStrategyBase.MapBipolar"/> (50 = neutral).
/// </remarks>
public class DLControlStrategy : ControlStrategyBase
{
    /// <inheritdoc/>
    public override string Name => "DLControl";

    /// <summary>
    /// Deadband for finger outputs [0,1]. Values below this threshold map to 0.
    /// Adjustable at runtime from UI.
    /// </summary>
    public double FingerDeadband { get; set; } = 0.08;

    /// <summary>
    /// Deadband for wrist outputs [-1,+1]. Absolute values below this snap to neutral (50).
    /// Adjustable at runtime from UI.
    /// </summary>
    public double WristDeadband { get; set; } = 0.15;

    /// <inheritdoc/>
    protected override void InitializeDefaults()
    {
        Set(DegreesOfActuation.ThumbFlexion,             0);
        Set(DegreesOfActuation.ThumbRotation,            0);
        Set(DegreesOfActuation.Index,                    0);
        Set(DegreesOfActuation.Middle,                   0);
        Set(DegreesOfActuation.Ring,                     0);
        Set(DegreesOfActuation.Little,                   0);
        Set(DegreesOfActuation.WristFlexionExtension,    50);  // neutral
        Set(DegreesOfActuation.WristPronationSupination, 50);  // neutral
        Set(DegreesOfActuation.WristUlnarRadial,         50);  // neutral
    }

    /// <inheritdoc/>
    public override void ProcessPrediction(Vector<double> prediction)
    {
        if (prediction is null || prediction.Count < 8) return;

        // -- Fingers: [0,1] -> [0,100] --------------------------------
        MapDirectWithDeadband(DegreesOfActuation.Index,        prediction, 0, FingerDeadband);
        MapDirectWithDeadband(DegreesOfActuation.Middle,       prediction, 1, FingerDeadband);
        MapDirectWithDeadband(DegreesOfActuation.Ring,         prediction, 2, FingerDeadband);
        MapDirectWithDeadband(DegreesOfActuation.Little,       prediction, 3, FingerDeadband);
        MapDirectWithDeadband(DegreesOfActuation.ThumbFlexion, prediction, 4, FingerDeadband);

        // -- Wrist: [-1,+1] -> [0,100]  (0 = 50 = neutral) -----------
        // WristFlexionExtension DOA: 100=extension, 0=flexion
        // Model output[5]: +1=flexion, -1=extension → invert to match
        MapBipolar(DegreesOfActuation.WristFlexionExtension,    prediction, 5, invert: true, deadband: WristDeadband);
        MapBipolar(DegreesOfActuation.WristPronationSupination, prediction, 6, deadband: WristDeadband);
        MapBipolar(DegreesOfActuation.WristUlnarRadial,         prediction, 7, deadband: WristDeadband);
    }

    /// <summary>
    /// Maps a [0,1] prediction to [0,100] with a deadband below which the output is 0.
    /// </summary>
    private void MapDirectWithDeadband(DegreesOfActuation doa, Vector<double> prediction, int index, double deadband)
    {
        var val = Get(prediction, index);
        if (val < deadband) val = 0.0;
        Set(doa, val * 100);
    }
}
