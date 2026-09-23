# LSL

[Block Catalogue](../block-catalogue.md) / Streaming

A Lab Streaming Layer endpoint that is either an outlet (pushes pipeline data onto the network) or an inlet (pulls a resolved network stream in), selected by `Params[0]`.

## Input and output

| Property | Value |
|---|---|
| **Type** | `lsl`, `lslblock` |
| **Inputs** | exactly 1 in **both** modes |
| **Consumes** | outlet only: `Vector<double>`, `Matrix<double>` (one sample per row), `(string, Vector<double>)`, `(string, Matrix<double>)`. Inlet ignores upstream data |
| **Publishes** | `Vector<double>` — outlet re-publishes each pushed sample; inlet publishes one vector per pulled chunk row, from its reader thread |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | Mode | string | `outlet` | Only the exact word `inlet` (case-insensitive) selects inlet; anything else means outlet |
| 1 | StreamName | string | `MOSAIC` | LSL stream name |
| 2 | StreamType | string | `EMG` | LSL stream type |
| 3 | SampleRate / Timeout | double | 2000 / 5 | Outlet: nominal rate in Hz. Inlet: resolve timeout in seconds |
| 4 | Channels / ChunkLen | int | 0 / 64 | Outlet: channel count (0 = infer from first sample). Inlet: max chunk length (≤0 coerced to 64) |

## Requirements and use

Choose inlet or outlet mode and configure matching stream metadata. An inlet needs a
discoverable stream; an outlet needs upstream data. Both modes require the native LSL
runtime for the platform.

The public repository retains the MIT-licensed C# binding but does not redistribute
`lsl.dll`. Download liblsl, then build with `-p:EnableLsl=true` and either place the
runtime at the documented Assets path or set `LslRuntime`. See
[Packages and optional devices](../getting-started.md#enable-the-native-lsl-runtime).

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Streaming.LSL). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
