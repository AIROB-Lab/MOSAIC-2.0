using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Enums;

using Vector = MathNet.Numerics.LinearAlgebra.Vector<double>;

namespace MOSAIC.Models.FlowControl;

/// <summary>
/// Streams a continuous label vector from an upstream source (dataglove, BodyRig,
/// SinGenerator, etc.) into the <see cref="TriggerBuffer"/> as per-frame regression
/// targets. Where the classic <see cref="Trigger"/> publishes one Vector per click and
/// holds it constant for the whole capture, <see cref="StreamTrigger"/> publishes
/// whichever Vector arrived most recently from upstream — so the buffer sees a
/// time-varying label stream paired with data, which is what regression on continuous
/// activations actually needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trigger-protocol compatibility.</b> The class name contains <c>"Trigger"</c>, which
/// is what <c>TriggerBuffer.OnReceive</c> checks to decide whether to treat the sender
/// as a trigger or as data. So this block plugs into existing buffer wiring with no
/// modifications: the buffer interprets every published Vector as "update current target",
/// every null as "end segment".
/// </para>
/// <para>
/// <b>Upstream input.</b> Expects a single input that publishes <see cref="Vector"/>s
/// — typically a <c>SinGenerator</c> (for testing), a future <c>DataGlove</c> block,
/// or any other vector-emitting source. The latest vector is cached in
/// <see cref="_latestLabel"/> and republished on each tick while streaming is active.
/// If the upstream rate is slower than the data rate, the cached value acts as
/// hold-last interpolation.
/// </para>
/// <para>
/// <b>UI control.</b> <see cref="StartStreamCommand"/> begins emission; the buffer
/// receives a non-null Vector and starts a segment. <see cref="StopStreamCommand"/>
/// publishes <c>null</c>, ending the segment. The streamer doesn't auto-start —
/// the user must explicitly hit "Start" to begin recording.
/// </para>
/// </remarks>
/// <example>
/// JSON configuration:
/// <code>
/// {
///   "ActivationSource": {
///     "Type": "SinGenerator",
///     "Inputs": ["Clock"],
///     "DesiredRate": 30,
///     "Params": [5, 1.0, 0.5, 0.0, 1.2566]
///   },
///   "StreamTrigger": {
///     "Type": "StreamTrigger",
///     "Inputs": ["ActivationSource"]
///   }
/// }
/// </code>
/// </example>
public sealed partial class StreamTrigger : BaseBlock
{
    #region State

    /// <summary>
    /// Most recently received upstream label. Held across upstream ticks so that
    /// downstream consumers always see the latest known target, even if the
    /// upstream rate differs from the data rate (hold-last semantics).
    /// </summary>
    private Vector? _latestLabel;

    #endregion

    #region Observable Properties

    /// <summary>
    /// When <c>true</c>, every upstream label received is forwarded to downstream
    /// consumers (i.e. the buffer). When <c>false</c>, upstream labels are still
    /// cached but nothing is published — so toggling to <c>true</c> resumes from
    /// the latest known value rather than waiting for the next upstream tick.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isStreaming;

    /// <summary>
    /// Human-readable identifier for the current recording session, written into
    /// the buffer's per-segment Label field. Useful for distinguishing sessions
    /// in the buffer's CSV export.
    /// </summary>
    [ObservableProperty]
    private string _sessionName = "session";

    /// <summary>Number of vectors published since streaming started (for UI counter).</summary>
    [ObservableProperty]
    private long _publishedCount;

    /// <summary>Number of upstream labels received (regardless of streaming state).</summary>
    [ObservableProperty]
    private long _receivedCount;

    /// <summary>Most recent label values for the live readout, as a comma-separated string.</summary>
    [ObservableProperty]
    private string _latestLabelText = "—";

    /// <summary>Computed status string for the card badge.</summary>
    public string StatusText => IsStreaming
        ? $"Streaming · {PublishedCount} frames"
        : (_latestLabel is null ? "Waiting for upstream…" : "Idle (ready)");

    #endregion

    #region Public API

    /// <summary>Current cached upstream label (read-only snapshot for inspection).</summary>
    public Vector? LatestLabel => _latestLabel;

    #endregion

    #region Constructor & Factory

    public StreamTrigger(string name, double desiredRate) : base(name, desiredRate)
    {
    }

    /// <summary>Recordings from this block are named <c>&lt;block&gt;_stream.csv</c>.</summary>
    protected override string DumpFilePrefix => $"{Name}_stream";

    public static StreamTrigger ConfigureInput(IServiceProvider sp, JsonModel m)
    {
        var rate  = m.DesiredRate ?? 0.0;
        var block = ActivatorUtilities.CreateInstance<StreamTrigger>(sp, m.Name ?? "StreamTrigger", rate);

        // Optional session name as first Param so a JSON-defined session label can
        // travel with the recording without a UI step.
        if (m.Params is { Count: > 0 } && m.Params[0] is string sessionName && !string.IsNullOrWhiteSpace(sessionName))
            block.SessionName = sessionName;

        return block;
    }

    #endregion

    #region JSON Export

    protected override string JsonTypeName => "StreamTrigger";

    protected override IReadOnlyList<object>? GetJsonParams()
        => string.IsNullOrWhiteSpace(SessionName) ? null : new List<object> { SessionName };

    #endregion

    #region Data Pipeline

    /// <summary>
    /// Receives a Vector from upstream and forwards it as a trigger-protocol
    /// message when streaming. Non-Vector inputs are ignored — this block only
    /// makes sense as a label-source proxy.
    /// </summary>
    protected override void OnReceive(object sender, object? data)
    {
        if (data is not Vector v) return;

        _latestLabel  = v;
        ReceivedCount++;
        LatestLabelText = FormatVector(v);

        if (!IsStreaming) return;

        // Trigger protocol: publishing a Vector means "active target is this".
        // The buffer caches it and queues a row for every data frame arriving
        // until the next Vector update (or null for stop).
        Publish(v);
        PublishedCount++;
    }

    #endregion

    #region Commands

    /// <summary>
    /// Begin streaming. Publishes the cached latest label immediately so the
    /// buffer can open a segment without waiting for the next upstream tick;
    /// if no upstream label has been seen yet, the buffer will start once the
    /// first one arrives.
    /// </summary>
    [RelayCommand]
    private void StartStream()
    {
        if (IsStreaming) return;
        IsStreaming    = true;
        PublishedCount = 0;

        if (_latestLabel is not null)
        {
            Publish(_latestLabel);
            PublishedCount++;
        }
        Debug.WriteLine($"[{Name}] Stream STARTED (session='{SessionName}', latest={LatestLabelText})");
    }

    /// <summary>
    /// Stop streaming. Publishes <c>null</c> so the buffer finalises the current
    /// segment and writes it to <c>dB</c>.
    /// </summary>
    [RelayCommand]
    private void StopStream()
    {
        if (!IsStreaming) return;
        IsStreaming = false;

        // Trigger protocol: publishing null ends the segment.
        Publish((object?)null);
        Debug.WriteLine($"[{Name}] Stream STOPPED ({PublishedCount} frames sent)");
    }

    /// <summary>
    /// Toggle convenience for the card's single Start/Stop button.
    /// </summary>
    [RelayCommand]
    private void ToggleStream()
    {
        if (IsStreaming) StopStream();
        else             StartStream();
    }

    #endregion

    #region Helpers

    private static string FormatVector(Vector v)
    {
        // Cap the readout at the first 8 elements so a dataglove with 22 channels
        // doesn't trash the UI. Truncation flagged with an ellipsis.
        const int maxShown = 8;
        var shown = v.Take(maxShown).Select(x => x.ToString("F2", CultureInfo.InvariantCulture));
        var tail  = v.Count > maxShown ? $", …(+{v.Count - maxShown})" : "";
        return $"[{string.Join(", ", shown)}{tail}]";
    }

    #endregion
}