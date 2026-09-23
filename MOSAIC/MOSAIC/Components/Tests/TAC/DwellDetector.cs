namespace MOSAIC.Components.Tests.TAC;

/// <summary>
/// Tracks whether a continuous hold within a distance threshold has been maintained
/// for the required dwell duration.
/// </summary>
/// <remarks>
/// <para>
/// Each call to <see cref="Update"/> advances the state machine:
/// if <c>distance ≤ threshold</c>, the internal timer accumulates;
/// if the distance exceeds the threshold after being inside, an overshoot is counted
/// and the timer resets. Once the accumulated hold time reaches <see cref="DwellTime"/>,
/// <see cref="IsComplete"/> becomes <see langword="true"/>.
/// </para>
/// <para>
/// Call <see cref="Reset"/> at the start of each new trial.
/// </para>
/// </remarks>
public sealed class DwellDetector
{
    /// <summary>Required continuous hold duration in seconds.</summary>
    public double DwellTime { get; }

    /// <summary>Maximum L2 distance to count as "in target".</summary>
    public double Threshold { get; }

    /// <summary>Whether the prediction is currently within the threshold.</summary>
    public bool InTarget { get; private set; }

    /// <summary>Continuous time spent within the threshold since last entry.</summary>
    public double HoldTime { get; private set; }

    /// <summary>Hold progress towards dwell time [0, 1].</summary>
    public double HoldProgress { get; private set; }

    /// <summary>Number of times the prediction left the target zone this trial.</summary>
    public int Overshoots { get; private set; }

    /// <summary>Whether the dwell time has been fully satisfied.</summary>
    public bool IsComplete { get; private set; }

    private double _entryTime;

    /// <summary>
    /// Initializes a new <see cref="DwellDetector"/>.
    /// </summary>
    /// <param name="dwellTime">Required hold duration in seconds.</param>
    /// <param name="threshold">Maximum L2 distance for "in target".</param>
    public DwellDetector(double dwellTime, double threshold)
    {
        DwellTime = dwellTime;
        Threshold = threshold;
    }

    /// <summary>
    /// Advances the dwell state machine with the current distance and timestamp.
    /// </summary>
    /// <param name="distance">Current L2 distance to target.</param>
    /// <param name="now">Current time in seconds (monotonic).</param>
    public void Update(double distance, double now)
    {
        if (IsComplete) return;

        bool wasInTarget = InTarget;

        if (distance <= Threshold)
        {
            if (!InTarget)
            {
                InTarget = true;
                _entryTime = now;
            }

            HoldTime = now - _entryTime;
            HoldProgress = System.Math.Min(1.0, HoldTime / DwellTime);

            if (HoldTime >= DwellTime)
                IsComplete = true;
        }
        else
        {
            if (wasInTarget)
                Overshoots++;

            InTarget = false;
            HoldTime = 0;
            HoldProgress = 0;
            _entryTime = now;
        }
    }

    /// <summary>
    /// Resets the detector for a new trial.
    /// </summary>
    public void Reset()
    {
        InTarget = false;
        HoldTime = 0;
        HoldProgress = 0;
        Overshoots = 0;
        IsComplete = false;
        _entryTime = 0;
    }
}