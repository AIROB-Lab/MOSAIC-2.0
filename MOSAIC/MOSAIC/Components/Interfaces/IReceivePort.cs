namespace MOSAIC.Components.Interfaces;

/// <summary>
/// Implemented by blocks that own an inbound socket, exposing the local port they listen on.
/// </summary>
/// <remarks>
/// <para>
/// The port is not merely a transport detail for some devices — it identifies the hardware. A
/// BodyRig 2 node derives its UDP port from the sensor number set on the device itself ("11"
/// followed by the number, so node 18 transmits on 11018), which makes the port the only place
/// the node's identity survives into a pipeline that has otherwise reduced it to anonymous
/// floats.
/// </para>
/// <para>
/// Declared as an interface so code that recovers device identity from a graph depends on
/// "a block with a receive port" rather than on a concrete socket type — which also keeps such
/// code testable without binding real sockets on fixed port numbers.
/// </para>
/// </remarks>
public interface IReceivePort
{
    /// <summary>Local port this block listens on.</summary>
    int ReceivePort { get; }
}
