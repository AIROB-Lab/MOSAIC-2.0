namespace MOSAIC.Components.Manager.Serial;

/// <summary>
/// Immutable snapshot of a <c>SerialSender</c>'s traffic counters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a snapshot:</b> the counters are written from two threads — the pipeline thread enqueues,
/// the writer thread transmits — so reading them one property at a time would let a caller observe a
/// torn view. <c>GetStats</c> samples them together and hands back a value type that cannot change
/// underneath the reader.
/// </para>
/// <para>
/// <b>Invariant:</b> <c>Enqueued == Written + Dropped + Pending</c> holds at every sampling instant,
/// with one exception: a line that has been dequeued but whose write has not yet returned is counted
/// in none of the three. It settles into <see cref="Written"/> or <see cref="Dropped"/> as soon as
/// the write resolves, and <c>Dispose</c> settles it on the writer's behalf if the write never does,
/// so the stricter <c>Enqueued == Written + Dropped</c> is exact on any drained queue — including
/// after a disposal that had to abandon a stalled port.
/// </para>
/// </remarks>
/// <param name="Enqueued">Samples handed to the queue, whether or not they survived.</param>
/// <param name="Written">Lines successfully written to the port.</param>
/// <param name="Dropped">
/// Samples discarded: rejected by the queue policy, lost to a failed write, or abandoned unsent at
/// disposal.
/// </param>
/// <param name="Timeouts">
/// Writes abandoned after <c>writeTimeoutMs</c>. A subset of <see cref="Dropped"/>; a rising count
/// means the receiver is not draining its buffer.
/// </param>
/// <param name="Errors">Port failures recorded, counting both timeouts and link errors.</param>
/// <param name="DumpErrors">
/// Failures writing the wire dump. Independent of the counters above: a dump failure never costs a
/// line, it only means the file is an incomplete record of one that was sent successfully.
/// </param>
/// <param name="Reconnects">Successful opens after the first one.</param>
/// <param name="Pending">Lines currently queued and not yet handed to the port.</param>
/// <param name="IsConnected">Whether the port was open at the sampling instant.</param>
/// <param name="LastError">Message of the most recent failure, or <see langword="null"/>.</param>
public readonly record struct SerialSenderStats(
    long Enqueued,
    long Written,
    long Dropped,
    long Timeouts,
    long Errors,
    long DumpErrors,
    long Reconnects,
    int Pending,
    bool IsConnected,
    string? LastError);
