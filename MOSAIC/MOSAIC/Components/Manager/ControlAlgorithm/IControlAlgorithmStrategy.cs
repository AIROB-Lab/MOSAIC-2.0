using System;
using System.Collections.Generic;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Components.Enums;

namespace MOSAIC.Components.Manager.ControlAlgorithm;

/// <summary>
/// Interface for control algorithm strategies.
/// Implement this (or extend <see cref="ControlStrategyBase"/>) to create custom control algorithms.
/// </summary>
public interface IControlAlgorithmStrategy
{
    /// <summary>
    /// Name of this strategy (used for JSON export and UI display).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Processes a prediction vector and updates actuation values.
    /// </summary>
    void ProcessPrediction(Vector<double> prediction);

    /// <summary>
    /// Gets the current DOA actuation values (typically [0,100]).
    /// </summary>
    Dictionary<DegreesOfActuation, double> GetControlDict();

    /// <summary>
    /// Resets all DOA values to defaults.
    /// </summary>
    void Reset();
}

/// <summary>
/// Base class for control strategies with helper methods for safe, clean code.
/// Extend this class to create custom control algorithms.
/// </summary>
/// <example>
/// <code>
/// public class MyStrategy : ControlStrategyBase
/// {
///     public override string Name => "MyStrategy";
///     
///     protected override void InitializeDefaults()
///     {
///         Set(DegreesOfActuation.Index, 0);
///         Set(DegreesOfActuation.Middle, 0);
///     }
///     
///     public override void ProcessPrediction(Vector&lt;double&gt; prediction)
///     {
///         Set(DegreesOfActuation.Index, Get(prediction, 0) * 100);
///         Set(DegreesOfActuation.Middle, Get(prediction, 1) * 100);
///     }
/// }
/// </code>
/// </example>
public abstract class ControlStrategyBase : IControlAlgorithmStrategy
{
    public abstract string Name { get; }

    protected readonly Dictionary<DegreesOfActuation, double> ControlDict = new();

    protected ControlStrategyBase()
    {
        InitializeDefaults();
    }

    /// <summary>
    /// Initialize DOA default values. Override this in derived classes.
    /// </summary>
    protected abstract void InitializeDefaults();

    public abstract void ProcessPrediction(Vector<double> prediction);

    public Dictionary<DegreesOfActuation, double> GetControlDict() => ControlDict;

    public void Reset() => InitializeDefaults();

    #region Helper Methods

    /// <summary>
    /// Safely gets a value from the prediction vector.
    /// Returns 0 if index is out of bounds.
    /// </summary>
    protected static double Get(Vector<double> prediction, int index)
    {
        return index >= 0 && index < prediction.Count ? prediction[index] : 0;
    }

    /// <summary>
    /// Sets a DOA value, clamped to [0, 100].
    /// </summary>
    protected void Set(DegreesOfActuation doa, double value)
    {
        ControlDict[doa] = Math.Clamp(value, 0, 100);
    }

    /// <summary>
    /// Adds to a DOA value, result clamped to [0, 100].
    /// </summary>
    protected void Add(DegreesOfActuation doa, double delta)
    {
        var current = ControlDict.TryGetValue(doa, out var v) ? v : 0;
        ControlDict[doa] = Math.Clamp(current + delta, 0, 100);
    }

    /// <summary>
    /// Maps a prediction value [0,1] directly to DOA [0,100].
    /// </summary>
    protected void MapDirect(DegreesOfActuation doa, Vector<double> prediction, int index)
    {
        Set(doa, Get(prediction, index) * 100);
    }

    /// <summary>
    /// Maps two opposing predictions to a centered DOA value.
    /// Result: 50 when equal, 0 when negative dominates, 100 when positive dominates.
    /// </summary>
    protected void MapOpposing(DegreesOfActuation doa, Vector<double> prediction, int positiveIdx, int negativeIdx)
    {
        var pos = Get(prediction, positiveIdx);
        var neg = Get(prediction, negativeIdx);
        Set(doa, (pos - neg + 1) / 2 * 100);
    }

    /// <summary>
    /// Maps a single bipolar prediction in [−1, +1] to DOA [0, 100].
    /// 0.0 maps to 50 (neutral), −1 maps to 0, +1 maps to 100.
    /// Use this for regression outputs where a single channel encodes
    /// both directions (e.g. wrist flex/ext as one signed value).
    /// Set <paramref name="invert"/> to <c>true</c> to flip the sign
    /// when the model's positive direction is opposite to the DOA convention.
    /// <paramref name="deadband"/> suppresses values near zero — any
    /// absolute value below this threshold maps to 50 (neutral).
    /// </summary>
    protected void MapBipolar(DegreesOfActuation doa, Vector<double> prediction, int index,
                              bool invert = false, double deadband = 0.0)
    {
        var bipolar = Get(prediction, index);                          // [-1, +1]
        if (invert) bipolar = -bipolar;
        if (Math.Abs(bipolar) < deadband) bipolar = 0.0;
        Set(doa, Math.Clamp((bipolar + 1.0) * 50.0, 0, 100));        // [0, 100]
    }

    /// <summary>
    /// Applies incremental step control with deadband.
    /// Only changes value if activation exceeds deadband threshold.
    /// </summary>
    protected void StepControl(DegreesOfActuation doa, double positive, double negative, double deadband, double stepSize)
    {
        if (positive > deadband || negative > deadband)
        {
            var posStep = Math.Max(0, positive - deadband) * stepSize;
            var negStep = Math.Max(0, negative - deadband) * stepSize;
            Add(doa, posStep - negStep);
        }
    }

    #endregion
}