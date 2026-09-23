namespace MOSAIC.Components.Interfaces;

/// <summary>
/// Defines a dispatcher that broadcasts published values to one or more subscribers (blocks).
/// </summary>
/// <remarks>
/// <para>
/// Implementations manage a set of <see cref="ISubscriber"/> instances and forward values produced by a publisher
/// (typically a processing block) to those subscribers.
/// </para>
/// <para>
/// The dispatcher operates on runtime-typed payloads (<see cref="object"/>). Subscribers are responsible for
/// validating and casting the received values to the expected types.
/// </para>
/// </remarks>
public interface IOutputDispatcher
{
    /// <summary>
    /// Registers a subscriber so it will receive values dispatched by this dispatcher.
    /// </summary>
    /// <param name="subscriber">The subscriber (block) instance that will receive dispatched values.</param>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown if <paramref name="subscriber"/> is <see langword="null"/>.
    /// </exception>
    void AddSubscriber(ISubscriber subscriber);

    /// <summary>
    /// Unregisters a subscriber so it no longer receives dispatched values.
    /// </summary>
    /// <param name="subscriber">The subscriber (block) instance to remove.</param>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown if <paramref name="subscriber"/> is <see langword="null"/>.
    /// </exception>
    void RemoveSubscriber(ISubscriber subscriber);

    /// <summary>
    /// Dispatches a value to all registered subscribers.
    /// </summary>
    /// <param name="sender">The originator of the value (typically the publishing block).</param>
    /// <param name="value">The value payload to broadcast to subscribers.</param>
    /// <remarks>
    /// Implementations may choose synchronous or asynchronous delivery semantics. Callers should not assume
    /// ordering guarantees unless explicitly documented by the concrete dispatcher implementation.
    /// </remarks>
    void Dispatch(object sender, object value);
}
