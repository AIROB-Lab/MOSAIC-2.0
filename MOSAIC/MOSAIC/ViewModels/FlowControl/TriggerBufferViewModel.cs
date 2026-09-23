using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MathNet.Numerics.LinearAlgebra;
using MOSAIC.Models.FlowControl;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;
using Matrix = MathNet.Numerics.LinearAlgebra.Matrix<double>;

namespace MOSAIC.ViewModels.FlowControl;

/// <summary>
/// ViewModel for the <see cref="TriggerBuffer"/> block card.
/// </summary>
/// <remarks>
/// <para>
/// Polls <see cref="TriggerBuffer.dB"/> at 2 Hz via a <see cref="DispatcherTimer"/>
/// to detect new segments without modifying the block. When a change is detected,
/// <see cref="SegmentCount"/> and <see cref="Segments"/> are updated on the UI thread.
/// </para>
/// <para>
/// With the buffer's new per-row target shape, the displayed "target" column shows row 0
/// for classification-style segments (all rows identical) or a <c>"[N×D streaming]"</c>
/// summary when rows vary across time (e.g. captured via a <c>StreamTrigger</c>).
/// </para>
/// </remarks>
public sealed partial class TriggerBufferViewModel : ObservableObject, IDisposable
{
    #region Constants

    private const int PollIntervalMs = 500;

    #endregion

    #region Fields

    private readonly TriggerBuffer _block;
    private readonly DispatcherTimer _pollTimer;
    private int _lastKnownCount = -1;

    #endregion

    #region Observable Properties

    public TriggerBuffer Block => _block;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    private int _segmentCount;

    [ObservableProperty] private bool _isCapturing;

    public ObservableCollection<SegmentSummary> Segments { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _savePath = string.Empty;

    [ObservableProperty] private bool _isSegmentsExpanded = true;

    #endregion

    #region Constructor

    public TriggerBufferViewModel(TriggerBuffer block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };
        _pollTimer.Tick += PollBuffer;
        _pollTimer.Start();
    }

    #endregion

    #region Polling

    private void PollBuffer(object? sender, EventArgs e)
    {
        var count = _block.dB.Count;
        if (count == _lastKnownCount) return;

        _lastKnownCount = count;
        SegmentCount    = count;

        Segments.Clear();
        for (int i = 0; i < _block.dB.Count; i++)
        {
            var (label, targets, data) = _block.dB[i];
            Segments.Add(new SegmentSummary
            {
                Index       = i + 1,
                Label       = label,
                Target      = FormatTargets(targets),
                RowCount    = data.RowCount,
                ColumnCount = data.ColumnCount
            });
        }
    }

    /// <summary>
    /// Format a targets matrix for display. If every row is identical (the classic
    /// classification case) shows just the values; otherwise summarises as
    /// <c>"[N×D streaming]"</c> to flag that this came from a streaming trigger.
    /// </summary>
    private static string FormatTargets(Matrix targets)
    {
        if (targets.RowCount == 0 || targets.ColumnCount == 0) return "[]";

        // Cheap "all identical rows" check — compare last row to row 0 element-wise.
        // Almost-always true for click-driven Trigger captures (which hold a single
        // target the whole segment); always false for streaming-trigger captures.
        bool identicalRows = true;
        if (targets.RowCount > 1)
        {
            int last = targets.RowCount - 1;
            for (int j = 0; j < targets.ColumnCount; j++)
            {
                if (Math.Abs(targets[0, j] - targets[last, j]) > 1e-9)
                {
                    identicalRows = false;
                    break;
                }
            }
        }

        if (identicalRows)
            return FormatVector(targets.Row(0));

        return $"[{targets.RowCount}×{targets.ColumnCount} streaming]";
    }

    private static string FormatVector(Vector v)
        => $"[{string.Join(", ", v.Select(x => x.ToString("F0")))}]";

    #endregion

    #region Commands

    private bool CanSave()  => SegmentCount > 0 && !string.IsNullOrWhiteSpace(SavePath);
    private bool CanClear() => SegmentCount > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => _block.SaveToCsv(SavePath);

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        _block.Clear();
        Segments.Clear();
        SegmentCount    = 0;
        _lastKnownCount = 0;
    }

    /// <summary>
    /// Remove a single segment by its 1-based display index (the same number
    /// shown in the segment list). Re-polls so the rows shift up immediately
    /// rather than waiting for the next 500 ms polling tick.
    /// </summary>
    /// <param name="segment">The segment summary the row's ✕ button was bound to.</param>
    [RelayCommand]
    private void RemoveSegment(SegmentSummary? segment)
    {
        if (segment is null) return;
        var zeroBased = segment.Index - 1;
        if (_block.RemoveSegment(zeroBased))
        {
            // Force immediate refresh — bypass the polling timer so the user
            // sees the row disappear the moment they click ✕.
            _lastKnownCount = -1;
            PollBuffer(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void ToggleSegmentsExpanded() => IsSegmentsExpanded = !IsSegmentsExpanded;

    #endregion

    #region IDisposable

    public void Dispose() => _pollTimer.Stop();

    #endregion
}

/// <summary>
/// Display model for a single captured segment row in the card view.
/// </summary>
public sealed class SegmentSummary
{
    public int Index { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public string Shape => $"{RowCount} × {ColumnCount}";
}