using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.Streaming;

namespace MOSAIC.ViewModels.Streaming;

/// <summary>
/// ViewModel for <see cref="MockUltrasoundSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// Exposes one button per synthetic class so the user can swap the output
/// pattern with a single click, plus an auto-cycle toggle that walks through
/// every class on a configurable interval — useful for collecting balanced
/// training data via <c>TriggerBuffer</c> without baby-sitting the UI.
/// </para>
/// <para>
/// The block does the actual frame generation; the VM only exposes UI state
/// and timer logic. All class switches go through the block's
/// <see cref="MockUltrasoundSource.CurrentClass"/> property, which the model's
/// <c>OnReceive</c> reads each tick.
/// </para>
/// </remarks>
public partial class MockUltrasoundSourceViewModel : ObservableObject, IDisposable
{
    public MockUltrasoundSource MockUltrasound { get; }

    /// <summary>
    /// One <see cref="ClassButtonItem"/> per synthetic class. Populated from
    /// <see cref="MockUltrasoundSource.NumClasses"/> at construction and rebuilt
    /// when that count changes.
    /// </summary>
    public ObservableCollection<ClassButtonItem> ClassButtons { get; } = new();

    /// <summary>Auto-cycle on/off. Drives <see cref="_cycleTimer"/>.</summary>
    private bool _autoCycleEnabled;
    public bool AutoCycleEnabled
    {
        get => _autoCycleEnabled;
        set
        {
            if (SetProperty(ref _autoCycleEnabled, value))
            {
                OnPropertyChanged(nameof(AutoCycleStatusText));
                if (value) StartCycleTimer();
                else       StopCycleTimer();
            }
        }
    }

    /// <summary>Seconds spent on each class before advancing in auto-cycle mode.</summary>
    private double _cycleSeconds = 5.0;
    public double CycleSeconds
    {
        get => _cycleSeconds;
        set
        {
            if (SetProperty(ref _cycleSeconds, value))
            {
                OnPropertyChanged(nameof(AutoCycleStatusText));
                if (_cycleTimer is not null)
                    _cycleTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.1, value));
            }
        }
    }

    /// <summary>Default class names — match the single-finger model. Override via
    /// <see cref="SetClassNames"/> if the model has a different class set.</summary>
    private static readonly IReadOnlyList<string> DefaultClassNames =
        new[] { "THUMB", "INDEX", "MIDDLE", "RING", "PINKY",
                "Class 5", "Class 6", "Class 7", "Class 8" };

    /// <summary>Active class names, indexed by class ID.</summary>
    private List<string> _classNames = new();

    /// <summary>Periodic timer that drives the auto-cycle.</summary>
    private DispatcherTimer? _cycleTimer;

    /// <summary>Status text for the cycle row (e.g. "Cycling every 5.0 s").</summary>
    public string AutoCycleStatusText => AutoCycleEnabled
        ? $"Cycling every {CycleSeconds:F1} s"
        : "Manual (click a class)";

    public MockUltrasoundSourceViewModel(MockUltrasoundSource block)
    {
        MockUltrasound = block ?? throw new ArgumentNullException(nameof(block));

        _classNames.AddRange(DefaultClassNames);
        RebuildButtons();
        UpdateSelection();

        MockUltrasound.PropertyChanged += OnBlockPropertyChanged;
    }

    /// <summary>
    /// Override the default class-name labels (e.g. to match the trained model's
    /// 9-gesture set instead of the 5-finger default).
    /// </summary>
    public void SetClassNames(IReadOnlyList<string> names)
    {
        _classNames = new List<string>(names);
        RebuildButtons();
        UpdateSelection();
    }

    // ── Commands ───────────────────────────────────────────────────────

    /// <summary>Switch the source's current class. Bound to each class button.</summary>
    [RelayCommand]
    private void SelectClass(int classId)
    {
        if (classId < 0 || classId >= MockUltrasound.NumClasses) return;
        MockUltrasound.CurrentClass = classId;
        // Block's OnCurrentClassChanged fires PropertyChanged → UpdateSelection().
    }

    /// <summary>Toggle the auto-cycle timer.</summary>
    [RelayCommand]
    private void ToggleAutoCycle()
    {
        AutoCycleEnabled = !AutoCycleEnabled;
    }

    // ── Auto-cycle wiring ──────────────────────────────────────────────

    private void StartCycleTimer()
    {
        StopCycleTimer();
        _cycleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(0.1, CycleSeconds))
        };
        _cycleTimer.Tick += AdvanceClass;
        _cycleTimer.Start();
    }

    private void AdvanceClass(object? sender, EventArgs e)
    {
        var n = MockUltrasound.NumClasses;
        if (n <= 0) return;
        MockUltrasound.CurrentClass = (MockUltrasound.CurrentClass + 1) % n;
    }

    private void StopCycleTimer()
    {
        if (_cycleTimer is null) return;
        _cycleTimer.Stop();
        _cycleTimer.Tick -= AdvanceClass;
        _cycleTimer = null;
    }

    // ── Model events ───────────────────────────────────────────────────

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Anything that touches UI state goes onto the dispatcher; OnReceive
        // ticks come from a background thread, so PropertyChanged from
        // [ObservableProperty] setters can land there too.
        Dispatcher.UIThread.Post(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(MockUltrasoundSource.NumClasses):
                    RebuildButtons();
                    UpdateSelection();
                    break;
                case nameof(MockUltrasoundSource.CurrentClass):
                    UpdateSelection();
                    break;
            }
        }, DispatcherPriority.Background);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private void RebuildButtons()
    {
        ClassButtons.Clear();
        for (int i = 0; i < MockUltrasound.NumClasses; i++)
        {
            var label = i < _classNames.Count ? _classNames[i] : $"Class {i}";
            ClassButtons.Add(new ClassButtonItem(i, label));
        }
    }

    private void UpdateSelection()
    {
        int active = MockUltrasound.CurrentClass;
        foreach (var b in ClassButtons)
            b.IsActive = b.ClassId == active;
    }

    public void Dispose()
    {
        StopCycleTimer();
        MockUltrasound.PropertyChanged -= OnBlockPropertyChanged;
    }
}

/// <summary>
/// One entry in the class-selection row — the class index, its display label,
/// and an <see cref="IsActive"/> flag that the AXAML uses to highlight whichever
/// class is currently being emitted.
/// </summary>
public partial class ClassButtonItem : ObservableObject
{
    /// <summary>Class index passed to <c>SelectClassCommand</c>.</summary>
    public int    ClassId { get; }

    /// <summary>Display label shown on the button (e.g. "INDEX").</summary>
    public string Label   { get; }

    /// <summary>True when this class is the source's currently emitted class.</summary>
    [ObservableProperty] private bool _isActive;

    public ClassButtonItem(int classId, string label)
    {
        ClassId = classId;
        Label   = label;
    }
}