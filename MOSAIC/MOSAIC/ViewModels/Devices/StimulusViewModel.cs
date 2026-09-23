using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.Devices;
using MOSAIC.Visualization;

namespace MOSAIC.ViewModels.Devices;

/// <summary>
/// ViewModel for the <see cref="Stimulus"/> block.
/// Exposes FSM state, task progress, and start/stop controls for the card UI.
/// </summary>
public partial class StimulusViewModel : ObservableObject, IDisposable
{
    private readonly Stimulus _block;

    // ── Observable State ──

    /// <summary>Current FSM state name.</summary>
    [ObservableProperty] private string _state = "rest";

    /// <summary>Name of the current task.</summary>
    [ObservableProperty] private string _currentTaskName = "None";

    /// <summary>Whether the FSM is running.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsNotRunning))]
    private bool _isRunning;

    /// <summary>Inverse of <see cref="IsRunning"/> for XAML binding (Avalonia lacks negation syntax).</summary>
    public bool IsNotRunning => !IsRunning;

    /// <summary>Whether shuffle mode is enabled.</summary>
    [ObservableProperty] private bool _shuffleEnabled;

    /// <summary>Tasks remaining in the current run.</summary>
    [ObservableProperty] private int _tasksRemaining;

    /// <summary>Current target vector as formatted string.</summary>
    [ObservableProperty] private string _targetValueText = "—";

    // ── Computed Properties ──

    /// <summary>Underlying block reference.</summary>
    public Stimulus Block => _block;

    /// <summary>Block display name.</summary>
    public string Name => _block.Name;

    /// <summary>Formatted rate string.</summary>
    public string FrequencyText => $"{_block.DesiredRate:F0} Hz";

    /// <summary>Total number of tasks.</summary>
    public int TaskCount => _block.TaskCount;

    /// <summary>Capture duration in seconds.</summary>
    [ObservableProperty] private double _captureDuration;

    partial void OnCaptureDurationChanged(double value)
    {
        _block.CaptureDuration = value;
    }

    /// <summary>Task names for display.</summary>
    public ObservableCollection<string> TaskNames { get; } = new();

    // ── Visualization ──

    /// <summary>
    /// Visualization bundle for the target output waveform.
    /// </summary>
    public BlockVisualization Viz { get; } = new();

    // ══════════════════════════════════════════════════════
    //  Construction
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// Initializes a new <see cref="StimulusViewModel"/> and wires it to the given block.
    /// </summary>
    /// <param name="block">The Stimulus block to bind to.</param>
    public StimulusViewModel(Stimulus block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));
        _captureDuration = _block.CaptureDuration;

        _block.Viz = Viz;

        // Populate task names
        foreach (var name in _block.TaskNames)
            TaskNames.Add(name);

        // Subscribe to block events
        _block.PropertyChanged += OnBlockPropertyChanged;
        _block.OnFsmStateChanged += HandleStateChanged;
        _block.OnCompleted += HandleCompleted;
    }

    // ══════════════════════════════════════════════════════
    //  Event Handlers
    // ══════════════════════════════════════════════════════

    /// <summary>Relays observable property changes from the block.</summary>
    private void OnBlockPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(Stimulus.State):
                    State = _block.State;
                    break;
                case nameof(Stimulus.CurrentTaskName):
                    CurrentTaskName = _block.CurrentTaskName;
                    break;
                case nameof(Stimulus.IsRunning):
                    IsRunning = _block.IsRunning;
                    break;
                case nameof(Stimulus.TasksRemaining):
                    TasksRemaining = _block.TasksRemaining;
                    break;
                case nameof(Stimulus.TargetValue):
                    TargetValueText = _block.TargetValue is not null
                        ? FormatVector(_block.TargetValue)
                        : "—";
                    break;
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Handles FSM state transitions.</summary>
    private void HandleStateChanged(string newState, string? taskName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            State = newState;
            if (taskName is not null)
                CurrentTaskName = taskName;
        }, DispatcherPriority.Background);
    }

    /// <summary>Handles FSM completion.</summary>
    private void HandleCompleted()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsRunning = false;
            State = "end";
            CurrentTaskName = "Done";
        }, DispatcherPriority.Background);
    }

    // ══════════════════════════════════════════════════════
    //  Commands
    // ══════════════════════════════════════════════════════

    /// <summary>Starts the FSM.</summary>
    [RelayCommand]
    private void Start()
    {
        _block.ShuffleEnabled = ShuffleEnabled;
        // Pass the current UI order to the model before starting
        _block.SetTaskOrder(TaskNames.ToList());
        _block.Start();
    }

    /// <summary>Stops the FSM.</summary>
    [RelayCommand]
    private void Stop()
    {
        _block.Stop();
    }

    /// <summary>Resets the FSM to initial state (same as Stop, clears task progress).</summary>
    [RelayCommand]
    private void Reset()
    {
        _block.Stop();
        TasksRemaining = _block.TaskCount;
        State = "rest";
        CurrentTaskName = "None";
        TargetValueText = "—";
    }

    // ══════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════

    /// <summary>Formats a vector for compact display.</summary>
    private static string FormatVector(MathNet.Numerics.LinearAlgebra.Vector<double> v)
    {
        if (v.Count <= 6)
            return $"[{string.Join(", ", v.Select(x => x.ToString("F2")))}]";

        return
            $"[{string.Join(", ", v.Take(3).Select(x => x.ToString("F2")))}...{string.Join(", ", v.TakeLast(2).Select(x => x.ToString("F2")))}]";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _block.PropertyChanged -= OnBlockPropertyChanged;
        _block.OnFsmStateChanged -= HandleStateChanged;
        _block.OnCompleted -= HandleCompleted;
        Viz.Dispose();
    }
}