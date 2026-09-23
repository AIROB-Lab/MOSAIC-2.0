# UDP Sender

[Block Catalogue](../block-catalogue.md) / Streaming

Sends pipeline data to Unity's Streamlined Input Manager over UDP using its binary framing — count, value/data/subtype bytes, 8-byte timestamp, payload, UInt16 counter — choosing the serialisation by the upstream block's runtime class name.

## Input and output

| Property | Value |
|---|---|
| **Type** | `udpstreamlinedsender` |
| **Inputs** | exactly 1 declared, though the design (and the class's own JSON example, which wires three) presupposes several |
| **Consumes** | by upstream class name: `ControlAlgorithm`/`ControlAlgorithm2`/`ManualControl` → `Dictionary<DegreesOfActuation, double>`, one packet per entry; `Myo` → `Vector<double>` as sbyte EMG-raw; `Resampler` → `Vector<double>` as double EMG-ARV; anything else → `Vector<double>` as generic FMG-raw |
| **Publishes** | `Vector<double>` — dictionary values in enumeration order on the control path, the incoming vector unchanged otherwise |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | HostIP | string | `127.0.0.1` | Target IP or hostname |
| 1 | Port | int | 8080 | UDP destination port |

## Requirements and use

Use a receiver implementing the Streamlined Input Manager binary framing. This protocol includes metadata and is not interchangeable with a plain packed float array.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Streaming.UdpStreamlinedSender). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
