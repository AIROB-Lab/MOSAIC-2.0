namespace MOSAIC.Components.Interfaces;

/// <summary>
/// Defines a subscriber (block) that can receive values from an upstream publisher or dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// Subscribers typically register with an <see cref="IPublisher"/> (directly or via an <see cref="IOutputDispatcher"/>)
/// to participate in the MOSAIC publish–subscribe pipeline.
/// </para>
/// <para>
/// Payloads are passed as runtime-typed <see cref="object"/> values. Implementations are expected to validate and cast
/// the received <paramref name="value"/> to the type(s) they support.
/// </para>
/// <para>
/// Threading is determined by the publisher/dispatcher implementation; subscribers should be written to be thread-safe
/// unless the pipeline guarantees single-threaded delivery.
/// </para>
/// </remarks>
public interface ISubscriber
{
    /// <summary>
    /// Receives an input value from a publisher or dispatcher.
    /// </summary>
    /// <param name="sender">The originator of the value (typically the publishing block).</param>
    /// <param name="value">The value payload delivered to this subscriber.</param>
    void ReceiveInput(object sender, object value);
}