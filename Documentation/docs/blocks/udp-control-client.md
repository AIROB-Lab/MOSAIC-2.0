# UDP Control Client

[Block Catalogue](../block-catalogue.md) / Streaming

Sends control vectors with proportional and derivative gains and receives feedback.

## Input and output

| Property | Value |
|---|---|
| **Type** | `udpcontrolclient`, `udpcontrol` |
| **Inputs** | an upstream control vector for transmission |
| **Consumes** | `Vector<double>` |
| **Publishes** | received feedback and republished upstream vectors |

## Parameters

| Index | Parameter | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | ReceivePort | int | required | Local port, 1–65535 |
| 1 | Format | string | `float` | `float` or `double`; must match receiver format |
| 2 | RemoteHost | string | required | Remote IP address |
| 3 | RemotePort | int | required | Remote port, 1–65535 |
| 4 | Kp | double | 0.0 | Proportional gain |
| 5 | Kd | double | 0.0 | Derivative gain |

## Requirements and use

Supply at least four parameters. Every outgoing packet appends Kp and Kd in little-endian format; use only a receiver expecting those extra fields. This type has no receive-only configuration and currently has no palette descriptor.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Streaming.UDPControlClient). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
