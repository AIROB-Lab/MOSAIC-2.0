using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Models.Tests;

namespace MOSAIC.Components.Tests.TAC;

/// <summary>
/// Aggregates trial-level and summary metrics for a <see cref="MOSAIC.Models.Tests.TAC"/> test session,
/// and provides CSV export of the full trial history.
/// </summary>
/// <remarks>
/// <para>
/// This class is owned by a <see cref="MOSAIC.Models.Tests.TAC"/> block (via <see cref="MOSAIC.Models.Tests.TAC.Results"/>)
/// and should not be instantiated independently. It is observable so that ViewModels can
/// bind directly to the aggregate properties.
/// </para>
/// <para>
/// <b>Thread safety:</b> Property updates are expected to occur on the pipeline thread.
/// Bind from the UI thread via the standard dispatcher mechanism.
/// </para>
/// </remarks>
public sealed partial class TACResults : ObservableObject
{
    /// <summary>Name of the owning TAC block (used in export headers).</summary>
    private readonly string _blockName;

    /// <summary>Dwell time configuration (used in export headers).</summary>
    private readonly double _dwellTime;

    /// <summary>Success threshold configuration (used in export headers).</summary>
    private readonly double _successThreshold;

    private readonly List<double> _completionTimes = new();
    private readonly List<double> _pathEfficiencies = new();
    private readonly List<int> _overshootCounts = new();
    private readonly List<TrialRecord> _trialHistory = new();

    /// <summary>Number of successfully completed trials.</summary>
    [ObservableProperty] private int _trialsCompleted;

    /// <summary>Total number of attempted trials.</summary>
    [ObservableProperty] private int _trialsAttempted;

    /// <summary>Completion rate as a percentage [0, 100].</summary>
    [ObservableProperty] private double _completionRate;

    /// <summary>Average completion time across successful trials (seconds).</summary>
    [ObservableProperty] private double _averageCompletionTime;

    /// <summary>Average path efficiency across successful trials [0, 1].</summary>
    [ObservableProperty] private double _averagePathEfficiency;

    /// <summary>Average overshoot count across all trials.</summary>
    [ObservableProperty] private double _averageOvershoot;

    /// <summary>Read-only access to the full trial history for export or analysis.</summary>
    public IReadOnlyList<TrialRecord> TrialHistory => _trialHistory;

    /// <summary>Number of trials recorded so far.</summary>
    public int TrialCount => _trialHistory.Count;
    
    /// <summary>
    /// Initializes a new <see cref="TACResults"/> instance.
    /// </summary>
    /// <param name="blockName">Name of the owning TAC block.</param>
    /// <param name="dwellTime">Configured dwell time (for export headers).</param>
    /// <param name="successThreshold">Configured success threshold (for export headers).</param>
    public TACResults(string blockName, double dwellTime, double successThreshold)
    {
        _blockName = blockName;
        _dwellTime = dwellTime;
        _successThreshold = successThreshold;
    }
    

    /// <summary>
    /// Records a completed trial (success or failure) and updates aggregate metrics.
    /// </summary>
    /// <param name="trial">The trial record to add.</param>
    public void RecordTrial(TrialRecord trial)
    {
        _trialHistory.Add(trial);
        _overshootCounts.Add(trial.Overshoots);

        if (trial.Success)
        {
            _completionTimes.Add(trial.CompletionTime);
            _pathEfficiencies.Add(trial.PathEfficiency);
        }

        UpdateAggregateMetrics();
    }

    /// <summary>Recomputes all aggregate metrics from the accumulated lists.</summary>
    private void UpdateAggregateMetrics()
    {
        CompletionRate = TrialsAttempted > 0
            ? (double)TrialsCompleted / TrialsAttempted * 100.0
            : 0;

        AverageCompletionTime = _completionTimes.Count > 0 ? Avg(_completionTimes) : 0;
        AveragePathEfficiency = _pathEfficiencies.Count > 0 ? Avg(_pathEfficiencies) : 0;
        AverageOvershoot = _overshootCounts.Count > 0
            ? _overshootCounts.Sum() / (double)_overshootCounts.Count
            : 0;
    }
    

    /// <summary>
    /// Resets all metrics, trial history, and aggregate accumulators to their initial state.
    /// </summary>
    public void Reset()
    {
        TrialsCompleted = 0;
        TrialsAttempted = 0;
        CompletionRate = 0;
        AverageCompletionTime = 0;
        AveragePathEfficiency = 0;
        AverageOvershoot = 0;
        _completionTimes.Clear();
        _pathEfficiencies.Clear();
        _overshootCounts.Clear();
        _trialHistory.Clear();
        Debug.WriteLine($"[{_blockName}] Metrics reset");
    }
    

    /// <summary>
    /// Exports the full trial history as a CSV string, including configuration headers
    /// and a summary footer.
    /// </summary>
    /// <returns>A CSV-formatted string ready to be written to a file.</returns>
    /// <example>
    /// <code>
    /// var csv = tacBlock.Results.ExportCsv();
    /// File.WriteAllText("tac_results.csv", csv);
    /// </code>
    /// </example>
    public string ExportCsv()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# TAC Test Results — {_blockName}");
        sb.AppendLine($"# Dwell: {_dwellTime:F2}s | Threshold: {_successThreshold:F3}");
        sb.AppendLine($"# Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        sb.AppendLine("Trial,TaskName,Result,CompletionTime_s,InitialDistance," +
                      "PathLength,PathEfficiency,Overshoots,TargetVector");

        foreach (var t in _trialHistory)
        {
            var tv = t.TargetVector is not null
                ? string.Join(";", t.TargetVector.Select(v => v.ToString("F4")))
                : "";

            sb.AppendLine($"{t.TrialNumber},{t.TaskName},{(t.Success ? "Success" : "Failed")}," +
                          $"{t.CompletionTime:F3},{t.InitialDistance:F4},{t.PathLength:F4}," +
                          $"{t.PathEfficiency:F4},{t.Overshoots},\"{tv}\"");
        }

        sb.AppendLine();
        sb.AppendLine($"# Summary: {TrialsCompleted}/{TrialsAttempted} ({CompletionRate:F1}%) | " +
                      $"Avg time: {AverageCompletionTime:F2}s | Avg eff: {AveragePathEfficiency:F3} | " +
                      $"Avg OS: {AverageOvershoot:F2}");

        return sb.ToString();
    }

    /// <summary>Computes the arithmetic mean of a list.</summary>
    private static double Avg(List<double> v)
    {
        if (v.Count == 0) return 0;
        double s = 0;
        foreach (var x in v) s += x;
        return s / v.Count;
    }
}

/// <summary>Immutable record of a single TAC trial result.</summary>
/// <param name="TrialNumber">Sequential trial number.</param>
/// <param name="TaskName">Name of the task (from the Stimulus).</param>
/// <param name="Success">Whether the trial was completed successfully.</param>
/// <param name="CompletionTime">Time to complete in seconds (0 if failed).</param>
/// <param name="InitialDistance">L2 distance at trial start.</param>
/// <param name="PathLength">Cumulative prediction path length during the trial.</param>
/// <param name="PathEfficiency">Ratio of initial distance to path length [0, 1].</param>
/// <param name="Overshoots">Number of times the prediction left the target zone.</param>
/// <param name="TargetVector">The target vector for this trial.</param>
public record TrialRecord(
    int TrialNumber, string TaskName, bool Success,
    double CompletionTime, double InitialDistance, double PathLength,
    double PathEfficiency, int Overshoots, Vector<double>? TargetVector);