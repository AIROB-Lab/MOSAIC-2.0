using System;
using System.Threading;
using System.Linq;
using Avalonia.Threading;
using MOSAIC.Diagnostics;

namespace MOSAIC.Components.Basics;

public abstract partial class BaseBlock
{
    private string? _lastError;
    private CsvDumper? _lastRecording;

    /// <summary>Last processing fault; activity monitoring never overwrites it.</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    public long RecordingDroppedRows => (Dumper ?? _lastRecording)?.DroppedRows ?? 0;
    public string? RecordingError => (Dumper ?? _lastRecording)?.LastError?.Message;
    public int QueuedOutputValues => (_dispatcher as OutputDispatcher)?.QueuedValues ?? 0;
    public long RejectedOutputValues => (_dispatcher as OutputDispatcher)?.RejectedValues ?? 0;
    public long DownstreamFailures => (_dispatcher as OutputDispatcher)?.SubscriberFailures ?? 0;
    public bool HasDiagnostics => LastError is not null || RecordingError is not null || RecordingDroppedRows > 0
        || RejectedOutputValues > 0 || DownstreamFailures > 0;
    public string DiagnosticText => !HasDiagnostics ? string.Empty : string.Join("; ", new[]
    {
        LastError,
        RecordingError is { } error ? $"Recording failed: {error}" : null,
        RecordingDroppedRows > 0 ? $"Recording: {RecordingDroppedRows} row(s) lost" : null,
        RejectedOutputValues > 0 ? $"Output: {RejectedOutputValues} value(s) rejected" : null,
        DownstreamFailures > 0 ? $"Downstream: {DownstreamFailures} processing failure(s)" : null
    }.Where(text => !string.IsNullOrEmpty(text)));

    /// <summary>Records a processing fault separately from activity and makes it visible on the card.</summary>
    public void ReportError(string message, Exception? exception = null)
    {
        var detail = exception is null ? message : $"{message} {exception.Message}";
        if (Interlocked.Exchange(ref _lastError, detail) == detail) return;
        if (exception is null) Log.Error(GetType().Name, $"{Name}: {detail}");
        else Log.Error(GetType().Name, Name, exception, message);
        Dispatcher.UIThread.Post(RefreshDiagnostics);
    }

    public void ClearError()
    {
        if (Interlocked.Exchange(ref _lastError, null) is null) return;
        Dispatcher.UIThread.Post(RefreshDiagnostics);
    }

    private (string? Error, string? RecordingError, long Dropped, int Queued, long Rejected, long Failures)
        _reportedDiagnostics;
    private bool _reportedHasDiagnostics;
    private string _reportedDiagnosticText = string.Empty;

    private void RefreshDiagnostics()
    {
        var next = (LastError, RecordingError, RecordingDroppedRows, QueuedOutputValues,
            RejectedOutputValues, DownstreamFailures);
        if (next == _reportedDiagnostics) return;
        var previous = _reportedDiagnostics;
        _reportedDiagnostics = next;
        if (previous.Error != next.LastError) OnPropertyChanged(nameof(LastError));
        if (previous.RecordingError != next.RecordingError) OnPropertyChanged(nameof(RecordingError));
        if (previous.Dropped != next.RecordingDroppedRows) OnPropertyChanged(nameof(RecordingDroppedRows));
        if (previous.Queued != next.QueuedOutputValues) OnPropertyChanged(nameof(QueuedOutputValues));
        if (previous.Rejected != next.RejectedOutputValues) OnPropertyChanged(nameof(RejectedOutputValues));
        if (previous.Failures != next.DownstreamFailures) OnPropertyChanged(nameof(DownstreamFailures));
        bool hasDiagnostics = HasDiagnostics;
        if (_reportedHasDiagnostics != hasDiagnostics)
        {
            _reportedHasDiagnostics = hasDiagnostics;
            OnPropertyChanged(nameof(HasDiagnostics));
        }
        string text = DiagnosticText;
        if (_reportedDiagnosticText != text)
        {
            _reportedDiagnosticText = text;
            OnPropertyChanged(nameof(DiagnosticText));
        }
    }
}
