# Serial Sender

[Block Catalogue](../block-catalogue.md) / Streaming

Writes formatted numeric samples to a serial port with a background writer.

## Input and output

| Property | Value |
|---|---|
| **Type** | `serialsender`, `serial` |
| **Inputs** | one (default block constraint) |
| **Consumes** | `Vector<double>`, `double[]`, or a scalar `double` |
| **Publishes** | the accepted numeric sample as a vector |

## Parameters

| Index | Parameter | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | PortName | string | required | Local serial device |
| 1 | BaudRate | int | 115200 | Port speed |
| 2 | Terminator | string | `\n` | Line ending |
| 3 | Separator | string | `,` | Field separator |
| 4 | Decimals | int | 2 | Output precision |
| 5 | QueueCapacity | int | 256 | Pending lines |
| 6 | WriteTimeoutMs | int | 500 | Write timeout |
| 7 | ResetDelayMs | int | 2000 | Device reset delay |
| 8 | NonFiniteValue | double | 0.0 | Replacement for NaN/infinity |
| 9 | AutoReconnect | bool | true | Retry failed connections |
| 10 | DropPolicy | string | `DropNewest` | Or `DropOldest` |

## Requirements and use

Match separator, precision and line ending to the receiving firmware. The bounded send queue can drop samples. An explicit Path enables both a full-precision data CSV and a wire-text log of successful writes; these intentionally differ when lines are rounded or dropped. This factory type currently has no palette descriptor.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Streaming.SerialSender). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
