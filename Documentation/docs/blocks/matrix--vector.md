# Matrix → Vector

[Block Catalogue](../block-catalogue.md) / Flow Control

Replays a buffered [timesteps × channels] frame one row per clock tick, turning windowed output back into a continuous sample-by-sample stream.

## Input and output

| Property | Value |
|---|---|
| **Type** | `matrix2vector`, `mosaic.models.matrix2vector` |
| **Inputs** | 2; see usage below |
| **Consumes** | timer input: any payload (tick only). Data input: `Matrix<double>` (stored whole, cursor reset) or `Vector<double>`. Anything else is dropped. |
| **Publishes** | `Vector<double>` of length = current frame's `ColumnCount`, one per timer tick |

## Parameters

not positional; `timer:` is tested first, then `timerBlockName:`; the last matching token wins. Mandatory.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| — | `timer:<Name>` or `timerBlockName:<Name>` | string | none — required | Name of the input treated as the clock |

## Requirements and use

Connect the matrix source and timing input as required by the input contract. Rows are replayed as channel vectors; choose a clock rate consistent with the samples inside the frame.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.Matrix2Vector). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
