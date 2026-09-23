using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Models.FlowControl;

namespace MOSAIC.ViewModels.FlowControl;

// ── Small model for one editable action row ───────────────────────────────────
public partial class ActionEntry : ObservableObject
{
    [ObservableProperty] private string _name = "action";
    [ObservableProperty] private string _values = "0;0;0;0;0;0";
}

/// <summary>
/// Live bar for one activation channel — used by the Trigger card to visualise
/// the modulated target during a recording. <see cref="Value"/> updates each
/// oscillation tick; <see cref="BaseVal"/> is the action's nominal weight for
/// this channel (used to dim inactive channels in the UI).
/// </summary>
public partial class ChannelBar : ObservableObject
{
    public int    Index   { get; init; }
    [ObservableProperty] private string _label  = string.Empty;
    [ObservableProperty] private double _value;
    [ObservableProperty] private double _baseVal;
}

/// <summary>
/// One item in <see cref="TriggerViewModel.ActionButtons"/>. Carries the action
/// name and an observable selection flag so the card's button styles can
/// highlight whichever action is currently selected via a <c>Classes.selected</c>
/// binding — no string-equality converter needed in the AXAML.
/// </summary>
public partial class ActionButtonItem : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    [ObservableProperty] private bool _isSelected;
}

public partial class TriggerViewModel : ObservableObject, IDisposable
{
    private readonly Trigger _trigger;
    private CancellationTokenSource? _captureCts;

    /// <summary>
    /// Timer used to animate <see cref="LiveBars"/> during the pre-record countdown
    /// so participants can sync to the movement before recording actually begins.
    /// In oscillation mode it runs <c>sin²(2π·f·t)</c> just like the real capture
    /// will; in classic mode it holds the action vector at full intensity. Stops
    /// the moment countdown ends — capture publishes its own modulated values
    /// through the block, taking over the bars without a gap.
    /// </summary>
    private System.Threading.Timer? _previewTimer;
    private long _previewStartTicks;

    /// <summary>
    /// Live per-channel activation bars driven by the block's <c>LiveActivation</c>.
    /// Each row stays in place between updates; only the <see cref="ChannelBar.Value"/>
    /// changes per tick so AXAML doesn't have to rebind / re-layout on every frame.
    /// </summary>
    public ObservableCollection<ChannelBar> LiveBars { get; } = new();

    public TriggerViewModel(Trigger trigger)
    {
        _trigger = trigger;

        // Set initial action — pick the first one if any are configured.
        var keys = _trigger.Actions.Keys.ToList();
        if (keys.Count > 0)
        {
            CurrentAction = keys[0];
            _trigger.SelectAction(CurrentAction);
        }

        // Build the action-button collection with per-item selection state.
        // Each ActionButtonItem carries the name + a bound IsSelected flag so
        // the AXAML can highlight the active one without comparing strings
        // through a converter on every change.
        foreach (var key in keys)
        {
            ActionButtons.Add(new ActionButtonItem
            {
                Name       = key,
                IsSelected = key == CurrentAction
            });
        }

        // JSON Config: pre-populate editable rows from the model
        foreach (var kvp in _trigger.Actions)
        {
            var vals = string.Join(";",
                kvp.Value.Select(v => v.ToString("G", CultureInfo.InvariantCulture)));
            ActionEntries.Add(new ActionEntry { Name = kvp.Key, Values = vals });
        }

        // Mirror oscillation state from the block so the UI reflects whatever the
        // JSON specified (or defaults if not).
        _oscillationMode        = _trigger.OscillationMode;
        _oscillationFrequencyHz = _trigger.OscillationFrequencyHz;
        _oscillationTickRateHz  = _trigger.OscillationTickRateHz;

        // Pre-populate the live bars with the current action's channel layout so
        // the user sees the row even before pressing record. Values are zeroed.
        RebuildLiveBarsFromCurrentAction();

        // Subscribe to block changes so the bars update on every modulated publish.
        // Marshal onto the UI thread — the oscillation timer fires on a worker.
        _trigger.PropertyChanged += OnTriggerPropertyChanged;
    }

    private void OnTriggerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Trigger.LiveActivation)) return;
        var snapshot = _trigger.LiveActivation;
        if (snapshot is null) return;

        // Copy the values out *before* posting to UI thread — the block may
        // mutate / replace LiveActivation again before the dispatch runs.
        var values = snapshot.ToArray();
        Dispatcher.UIThread.Post(() => SyncLiveBars(values), DispatcherPriority.Background);
    }

    /// <summary>
    /// Rebuild the live-bar collection from the current action vector. Used at
    /// construction and after the user picks a different action so the row
    /// already shows the right channels before pressing record.
    /// </summary>
    private void RebuildLiveBarsFromCurrentAction()
    {
        LiveBars.Clear();
        var vec = _trigger.CurrentActionVector;
        if (vec is null) return;
        for (int i = 0; i < vec.Count; i++)
        {
            LiveBars.Add(new ChannelBar
            {
                Index   = i,
                Label   = $"Ch{i}",
                Value   = 0.0,
                BaseVal = vec[i]
            });
        }
    }

    /// <summary>
    /// Push a fresh activation snapshot into the bar collection. Grows / shrinks
    /// the collection in place so AXAML doesn't have to rebind on every tick.
    /// </summary>
    private void SyncLiveBars(double[] values)
    {
        // Resize.
        while (LiveBars.Count < values.Length)
        {
            var i = LiveBars.Count;
            LiveBars.Add(new ChannelBar { Index = i, Label = $"Ch{i}", Value = 0.0 });
        }
        while (LiveBars.Count > values.Length)
            LiveBars.RemoveAt(LiveBars.Count - 1);

        // Update values in place — ObservableProperty on ChannelBar makes the bar
        // graphics react without replacing the items.
        for (int i = 0; i < values.Length; i++)
            LiveBars[i].Value = values[i];
    }

    // ── Pre-record preview animation ──────────────────────────────────────────
    //
    // During the pre-record countdown the block isn't publishing anything yet
    // (StartCapture hasn't been called). Without help, the bars sit at zero and
    // the participant can't anticipate the movement. The helpers below run a
    // timer that mirrors what the actual capture would produce, so the
    // countdown serves as a warm-up: the participant sees the sine sweep (or
    // static gesture in classic mode) starting from the moment they hit Record
    // and can match the rhythm before the buffer starts collecting.

    private void StartPreviewTimer()
    {
        StopPreviewTimer();
        _previewStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        // Match the capture's tick rate when oscillating so the visual rhythm
        // is identical; in classic mode update slowly since the bars don't
        // change.
        int periodMs = _trigger.OscillationMode
            ? Math.Max(16, (int)Math.Round(1000.0 / Math.Max(10.0, _trigger.OscillationTickRateHz)))
            : 200;
        _previewTimer = new System.Threading.Timer(PreviewTick, null, 0, periodMs);
    }

    private void StopPreviewTimer()
    {
        _previewTimer?.Dispose();
        _previewTimer = null;
    }

    public void Dispose()
    {
        _trigger.PropertyChanged -= OnTriggerPropertyChanged;
        StopCapture();
        StopPreviewTimer();
    }

    private void PreviewTick(object? state)
    {
        if (!IsCountingDown) return;
        var baseVec = _trigger.CurrentActionVector;
        if (baseVec is null) return;

        double[] values;
        if (_trigger.OscillationMode)
        {
            double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - _previewStartTicks)
                           / (double)System.Diagnostics.Stopwatch.Frequency;
            double s = Math.Sin(2.0 * Math.PI * _trigger.OscillationFrequencyHz * elapsed);
            double envelope = s * s; // sin² ∈ [0, 1]
            values = new double[baseVec.Count];
            for (int i = 0; i < baseVec.Count; i++)
                values[i] = baseVec[i] * envelope;
        }
        else
        {
            // Classic mode — show the full target so participants see which
            // channels they're recording.
            values = baseVec.ToArray();
        }

        Dispatcher.UIThread.Post(() => SyncLiveBars(values), DispatcherPriority.Background);
    }

    public string Name => _trigger.Name;

    /// <summary>
    /// Action buttons rendered in the card. Each item carries the action name
    /// and an observable <see cref="ActionButtonItem.IsSelected"/> flag so the
    /// AXAML can highlight whichever button is currently selected via a Classes
    /// binding (no per-item converters needed).
    /// </summary>
    public ObservableCollection<ActionButtonItem> ActionButtons { get; } = new();

    [ObservableProperty] private string _currentAction = "None";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isCapturing;

    /// <summary>
    /// True during the pre-record warm-up window (the "3, 2, 1…" before the
    /// trigger actually fires). Lets the AXAML swap the REC badge for a
    /// READY badge and keep the Record button hidden while we count.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isCountingDown;

    /// <summary>
    /// Seconds of pre-record warm-up. 0 disables the countdown entirely (the
    /// trigger fires immediately when Record is clicked — original behaviour
    /// for users who don't need probe-positioning time).
    /// </summary>
    [ObservableProperty] private int _countdownSeconds = 3;

    /// <summary>
    /// Live counter ticking down once per second from <see cref="CountdownSeconds"/>
    /// to zero. Displayed as the big "READY: 3s / 2s / 1s" indicator in the card.
    /// </summary>
    [ObservableProperty] private int _countdownRemaining;

    /// <summary>True when the block is doing nothing — neither counting down nor recording.</summary>
    public bool IsIdle => !IsCapturing && !IsCountingDown;

    [ObservableProperty] private int _captureDuration = 5;

    [ObservableProperty] private bool _isJsonConfigExpanded = false;
    public ObservableCollection<ActionEntry> ActionEntries { get; } = new();

    // ── Oscillation-mode controls ─────────────────────────────────────────────
    //
    // The Trigger block already has these as plain properties (no observable
    // change notifications), so the VM holds the canonical UI state and pushes
    // updates to the block in the setters. Set once from the JSON-configured
    // values at construction; user edits via these properties propagate
    // immediately.

    /// <summary>
    /// When <see langword="true"/>, capture publishes a <c>sin²</c>-modulated
    /// version of the action vector at <see cref="OscillationTickRateHz"/> until
    /// stop. Each recording sweeps the action's active channels 0→1→0 repeatedly,
    /// giving the regression model the full intensity range in one recording.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OscillationStatusText))]
    private bool _oscillationMode;

    /// <summary>Sweep frequency in Hz (one full 0→1→0 cycle = 1/f seconds).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OscillationStatusText))]
    private double _oscillationFrequencyHz;

    /// <summary>
    /// How often the modulated target gets republished during a recording. Should
    /// match the data block's rate so the buffer sees one fresh target per frame.
    /// </summary>
    [ObservableProperty]
    private double _oscillationTickRateHz;

    /// <summary>Status string shown in the card when oscillation mode is on.</summary>
    public string OscillationStatusText => OscillationMode
        ? $"Oscillating · {OscillationFrequencyHz:F2} Hz ({1.0 / Math.Max(0.001, OscillationFrequencyHz):F1} s/cycle)"
        : "Classic constant target";

    partial void OnOscillationModeChanged(bool value)
        => _trigger.OscillationMode = value;

    partial void OnOscillationFrequencyHzChanged(double value)
    {
        if (value > 0) _trigger.OscillationFrequencyHz = value;
    }

    partial void OnOscillationTickRateHzChanged(double value)
    {
        if (value > 0) _trigger.OscillationTickRateHz = value;
    }

    [RelayCommand]
    private void SelectAction(string? actionName)
    {
        if (string.IsNullOrEmpty(actionName) || IsCapturing || IsCountingDown) return;

        CurrentAction = actionName;
        _trigger.SelectAction(actionName);
        UpdateActionSelection();
        RebuildLiveBarsFromCurrentAction();
    }

    /// <summary>
    /// Sync the <see cref="ActionButtonItem.IsSelected"/> flag across all
    /// buttons so exactly the one matching <see cref="CurrentAction"/> is
    /// highlighted. Called from <see cref="SelectAction"/> and after JSON
    /// config changes that might add or remove buttons.
    /// </summary>
    private void UpdateActionSelection()
    {
        foreach (var item in ActionButtons)
            item.IsSelected = string.Equals(item.Name, CurrentAction, StringComparison.Ordinal);
    }

    [RelayCommand]
    private void StartCapture()
    {
        if (IsCapturing || IsCountingDown || string.IsNullOrEmpty(CurrentAction)) return;

        // One shared cancellation token covers both the countdown phase and the
        // subsequent capture phase — clicking Stop at any point unwinds both.
        _captureCts?.Cancel();
        _captureCts?.Dispose();
        _captureCts = new CancellationTokenSource();
        var token = _captureCts.Token;

        if (CountdownSeconds <= 0)
        {
            BeginCaptureNow(token);
            return;
        }

        IsCountingDown    = true;
        CountdownRemaining = CountdownSeconds;
        StartPreviewTimer();

        Task.Run(async () =>
        {
            try
            {
                for (int i = CountdownSeconds; i >= 1; i--)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => CountdownRemaining = i,
                                                          DispatcherPriority.Background);
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }

                if (token.IsCancellationRequested) return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StopPreviewTimer();
                    IsCountingDown     = false;
                    CountdownRemaining = 0;
                    BeginCaptureNow(token);
                }, DispatcherPriority.Background);
            }
            catch (TaskCanceledException)
            {
                // Stop pressed during countdown — clean up UI state.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StopPreviewTimer();
                    IsCountingDown     = false;
                    CountdownRemaining = 0;
                    // Reset bars to nominal since no recording happened.
                    RebuildLiveBarsFromCurrentAction();
                }, DispatcherPriority.Background);
            }
        }, token);
    }

    /// <summary>
    /// Kicks off the actual capture (countdown is done at this point). Schedules
    /// the auto-stop for <see cref="CaptureDuration"/> seconds out.
    /// </summary>
    private void BeginCaptureNow(CancellationToken token)
    {
        IsCapturing = true;
        var duration = CaptureDuration;

        // Tell the model to start capture (publishes vector — or kicks off the
        // oscillation timer in oscillating mode).
        _trigger.StartCapture();

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(duration), token);

                if (!token.IsCancellationRequested)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _trigger.StopCapture();
                        IsCapturing = false;
                    }, DispatcherPriority.Background);
                }
            }
            catch (TaskCanceledException)
            {
                // Cancelled - that's fine
            }
        }, token);
    }

    [RelayCommand]
    private void StopCapture()
    {
        _captureCts?.Cancel();
        _captureCts?.Dispose();
        _captureCts = null;

        // Cancelling during countdown leaves the block untouched; we just clear
        // the visible state. Preview timer is also stopped here in case the
        // async TaskCanceledException handler hasn't run yet — StopPreviewTimer
        // is idempotent so this is safe.
        if (IsCountingDown)
        {
            StopPreviewTimer();
            IsCountingDown     = false;
            CountdownRemaining = 0;
            RebuildLiveBarsFromCurrentAction();
            return;
        }

        if (!IsCapturing) return;

        _trigger.StopCapture();
        IsCapturing = false;
    }

    [RelayCommand]
    private void ToggleJsonConfigExpanded() =>
        IsJsonConfigExpanded = !IsJsonConfigExpanded;

    [RelayCommand]
    private void AddActionEntry() =>
        ActionEntries.Add(new ActionEntry());

    [RelayCommand]
    private void RemoveActionEntry(ActionEntry entry) =>
        ActionEntries.Remove(entry);

    [RelayCommand]
    private void ApplyJsonConfig()
    {
        _trigger.Actions.Clear();

        foreach (var entry in ActionEntries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;

            var doubles = entry.Values
                .Split(';', StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                .Select(s => double.TryParse(s, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var d)
                    ? d
                    : 0.0)
                .ToArray();

            _trigger.Actions[entry.Name] =
                Vector<double>.Build.Dense(doubles);
        }

        // Re-push oscillation config — the block fields aren't touched by the
        // action-dict rebuild above, but doing this defensively keeps VM and
        // block in lockstep no matter what.
        _trigger.OscillationMode        = OscillationMode;
        _trigger.OscillationFrequencyHz = OscillationFrequencyHz;
        _trigger.OscillationTickRateHz  = OscillationTickRateHz;

        // Refresh the action-button panel to reflect the new set of keys.
        // Rebuilding the entire collection (rather than mutating in place) is
        // fine here — ApplyJsonConfig is rare and ItemsControl re-render cost
        // is negligible for the typical handful of actions.
        ActionButtons.Clear();
        foreach (var key in _trigger.Actions.Keys)
        {
            ActionButtons.Add(new ActionButtonItem
            {
                Name       = key,
                IsSelected = key == CurrentAction
            });
        }

        // Re-select first if current is gone
        if (ActionButtons.All(b => b.Name != CurrentAction))
        {
            CurrentAction = ActionButtons.FirstOrDefault()?.Name ?? "None";
            if (CurrentAction != "None")
                _trigger.SelectAction(CurrentAction);
            UpdateActionSelection();
        }
        RebuildLiveBarsFromCurrentAction();
    }
}
