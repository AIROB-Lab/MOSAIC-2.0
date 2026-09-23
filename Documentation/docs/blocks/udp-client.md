# UDP Client

[Block Catalogue](../block-catalogue.md) / Streaming

Bidirectional UDP endpoint: binds a local receive port and publishes every parsed datagram downstream, and — if an upstream and a remote endpoint are configured — also serialises upstream vectors out.

## Input and output

| Property | Value |
|---|---|
| **Type** | `udpclient` |
| **Inputs** | 0–1; see usage below |
| **Consumes** | `Vector<double>` only; other types are debug-logged and dropped, zero-length vectors dropped silently |
| **Publishes** | `Vector<double>` from two independent paths — received packets (channel count = length / 4 or / 8) on the socket IO thread, and republished upstream vectors on the pipeline thread |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | ReceivePort | int | 3342 | Local bind port; must be 1–65535 or `ArgumentException` |
| 1 | Format | string | `double` | Only `float` (4 B/ch) or `double` (8 B/ch); anything else throws |
| 2 | RemoteHost | string | *(none)* | Literal IP only; blank means receive-only |
| 3 | RemotePort | int | *(none)* | ≤0 or >65535 treated as absent |

## Requirements and use

Use an available local port and match the sender format. Construction starts receiving. Configure both remote host and port for outgoing data; use a literal IP address. Closing the block releases its socket.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Streaming.UDPClient). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
