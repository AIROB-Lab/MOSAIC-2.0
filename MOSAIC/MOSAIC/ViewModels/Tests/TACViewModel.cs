using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.Tests;
using MOSAIC.Visualization;
using TACResults = MOSAIC.Components.Tests.TAC.TACResults;

namespace MOSAIC.ViewModels.Tests;

/// <summary>
/// ViewModel for the <see cref="TAC"/> block.
/// </summary>
/// <remarks>
/// <para>
/// The View binds directly to <c>Block.*</c> for real-time observable properties
/// (distance, hold progress, trial elapsed, etc.) and to <c>Block.Results.*</c> for
/// aggregate metrics (completion rate, average time, etc.).
/// </para>
/// <para>
/// The ViewModel itself only manages trial result text, test-complete flag,
/// visualization wiring, and commands (reset / export).
/// </para>
/// </remarks>
public partial class TACViewModel : ObservableObject, IDisposable
{
    /// <summary>The underlying TAC block.</summary>
    private readonly TAC _block;

    /// <summary>Human-readable summary of the last trial result.</summary>
    [ObservableProperty] private string _lastTrialResult = "—";

    /// <summary>Whether the Stimulus has reached the <c>"end"</c> state.</summary>
    [ObservableProperty] private bool _isTestComplete;
    
    [ObservableProperty] private bool _isJsonConfigExpanded = false;

    [RelayCommand]
    private void ToggleJsonConfigExpanded() => IsJsonConfigExpanded = !IsJsonConfigExpanded;

    [ObservableProperty] private string _stimulusBlockName;
    [ObservableProperty] private double _dwellTime;
    [ObservableProperty] private double _successThreshold;

    /// <summary>The TAC block for direct binding from the View.</summary>
    public TAC Block => _block;

    /// <summary>Shortcut to <see cref="TAC.Results"/> for aggregate metric binding.</summary>
    public TACResults Results => _block.Results;

    /// <summary>Block display name.</summary>
    public string Name => _block.Name;
    
    public ObservableCollection<string> AvailableInputNames { get; } = [];
    /// <summary>Visualization bundle wired to the block.</summary>
    public BlockVisualization Viz { get; } = new();

    /// <summary>
    /// Initializes the ViewModel, wires visualization, and subscribes to trial events.
    /// </summary>
    /// <param name="block">The TAC block to present.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="block"/> is <see langword="null"/>.</exception>
    public TACViewModel(TAC block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));
        _block.Viz = Viz;
        _block.OnTrialCompleted += HandleTrialCompleted;
        _stimulusBlockName = _block.StimulusBlockName;
        _dwellTime         = _block.DwellTime;
        _successThreshold  = _block.SuccessThreshold;
        foreach (var name in _block.Inputs ?? [])
            AvailableInputNames.Add(name);
    }

    /// <summary>Formats and posts trial result text to the UI thread.</summary>
    private void HandleTrialCompleted(bool success, double time, double pathEff, int overshoots)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LastTrialResult = success
                ? $"✓ {time:F1}s  eff:{pathEff:P0}  overshoot:{overshoots}"
                : $"✗ Failed  overshoot:{overshoots}";

            if (_block.StimulusState == "end")
            {
                IsTestComplete = true;
                LastTrialResult += "  —  TEST COMPLETE";
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Resets all metrics and clears the result text.</summary>
    [RelayCommand]
    private void ResetMetrics()
    {
        _block.Results.Reset();
        LastTrialResult = "—";
        IsTestComplete = false;
    }

    /// <summary>Exports the full trial history to a CSV file on the desktop.</summary>
    [RelayCommand]
    private async Task ExportResults()
    {
        try
        {
            if (_block.Results.TrialCount == 0)
            {
                LastTrialResult = "⚠ No trials to export";
                return;
            }

            var csv = _block.Results.ExportCsv();
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var filename = $"TAC_{_block.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            var path = System.IO.Path.Combine(desktop, filename);

            await System.IO.File.WriteAllTextAsync(path, csv);

            LastTrialResult = $"⬇ Exported {_block.Results.TrialCount} trials → {filename}";
        }
        catch (Exception ex)
        {
            LastTrialResult = $"⚠ Export failed: {ex.Message}";
        }
    }

    /// <summary>Unsubscribes from block events and disposes visualization.</summary>
    public void Dispose()
    {
        _block.OnTrialCompleted -= HandleTrialCompleted;
        Viz.Dispose();
    }
}
