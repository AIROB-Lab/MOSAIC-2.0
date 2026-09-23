using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MOSAIC.Visualization.ScopeMonitor;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that suppresses all notifications during a batch,
/// then fires a single coalesced notification when the batch ends.
/// </summary>
/// <remarks>
/// <para>
/// The notification strategy matters critically for LiveCharts2's <c>VectorManager</c>.
/// A <c>CollectionChanged(Reset)</c> notification causes LiveCharts to fully re-initialise
/// its internal segment bookkeeping on the next <c>Measure()</c> pass. If a second
/// <c>Measure()</c> is already queued (via <c>UpdateThrottlerUnlocked</c>), the two
/// passes share a <c>VectorManager</c> whose state is half-rebuilt, triggering
/// <c>AddConsecutiveSegment</c> to throw <c>"This should not happen :("</c>.
/// </para>
/// <para>
/// The safe strategy: when the batch ends after a <see cref="Collection{T}.Clear"/>, fire nothing
/// if the collection is empty — there are no segments for <c>VectorManager</c> to track,
/// so there is no state to corrupt. LiveCharts will read the empty collection on its next
/// naturally-scheduled <c>Measure()</c> and produce a blank frame cleanly.
/// When the batch ends with data, fire a single <c>Reset</c> as before — this is safe
/// because the <c>VectorManager</c> has valid points to work with.
/// </para>
/// <para>
/// Additionally, if <see cref="IsChartReady"/> is <c>false</c> (chart not yet attached or
/// being destroyed), all notifications are suppressed unconditionally. Set this to
/// <c>true</c> only from <c>NotifyChartReady()</c>, called after the chart's first
/// <c>Measure()</c> confirms <c>VectorManager</c> is initialised.
/// </para>
/// </remarks>
public class BatchObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressing;

    /// <summary>
    /// When <c>false</c> (the default), all <see cref="INotifyCollectionChanged"/> and
    /// <see cref="INotifyPropertyChanged"/> notifications are suppressed unconditionally.
    /// Set to <c>true</c> only after the bound chart's first <c>Measure()</c> pass has
    /// confirmed <c>VectorManager</c> is ready to receive segment updates.
    /// </summary>
    public bool IsChartReady { get; set; }

    /// <summary>Begins a batch — all mutations are suppressed until <see cref="EndBatch"/>.</summary>
    public void BeginBatch() => _suppressing = true;

    /// <summary>
    /// Ends the batch. If <see cref="IsChartReady"/> is false, or the collection is empty
    /// after the batch, no notification is fired. Otherwise fires a single <c>Reset</c>.
    /// </summary>
    /// <remarks>
    /// Firing <c>Reset</c> on an empty collection is the trigger for the VectorManager crash:
    /// it causes LiveCharts to attempt a full re-layout with zero segments, which corrupts
    /// the segment linked-list and makes the next <c>Measure()</c> throw.
    /// </remarks>
    public void EndBatch()
    {
        if (!_suppressing) return;
        _suppressing = false;

        // Never notify if chart is not ready — no live VectorManager to receive it.
        if (!IsChartReady) return;

        // Never fire Reset on an empty collection — this is the specific sequence that
        // corrupts VectorManager. An empty collection means "nothing to draw"; LiveCharts
        // will read the empty state on its next scheduled Measure() without issue.
        if (Count == 0) return;

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppressing) return;
        if (!IsChartReady) return;
        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppressing) return;
        if (!IsChartReady) return;
        base.OnPropertyChanged(e);
    }
}