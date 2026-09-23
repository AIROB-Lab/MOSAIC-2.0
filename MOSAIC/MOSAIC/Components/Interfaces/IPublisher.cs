namespace MOSAIC.Components.Interfaces;

/// <summary>
/// Defines a publisher that can register and unregister subscribers (blocks) for downstream delivery.
/// </summary>
/// <remarks>
/// <para>
/// A publisher maintains a set of <see cref="ISubscriber"/> instances that should receive values produced
/// by the publisher. The actual delivery of values is typically performed by the concrete publisher itself
/// (e.g., via a dispatcher).
/// </para>
/// <para>
/// This interface does not prescribe payload typing. In MOSAIC, payloads are commonly passed as
/// runtime-typed <see cref="object"/> values, and subscribers are responsible for validating and casting
/// received values.
/// </para>
/// </remarks>
public interface IPublisher
{
    /// <summary>
    /// Registers a subscriber so it will receive values produced by this publisher.
    /// </summary>
    /// <param name="subscriber">The subscriber (block) instance to register.</param>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown if <paramref name="subscriber"/> is <see langword="null"/>.
    /// </exception>
    void AddSubscriber(ISubscriber subscriber);

    /// <summary>
    /// Unregisters a subscriber so it no longer receives values produced by this publisher.
    /// </summary>
    /// <param name="subscriber">The subscriber (block) instance to unregister.</param>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown if <paramref name="subscriber"/> is <see langword="null"/>.
    /// </exception>
    void RemoveSubscriber(ISubscriber subscriber);
}