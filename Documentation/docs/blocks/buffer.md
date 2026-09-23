# Buffer

[Block Catalogue](../block-catalogue.md) / Flow Control

Start/stop capture accumulator for batch training. The first vector of a segment becomes the Target label; every later vector becomes a data row.

## Input and output

| Property | Value |
|---|---|
| **Type** | `buffer`, `mosaic.models.buffer`, `imblocks.blocks.flowcontrol.buffer` |
| **Inputs** | exactly 1 (needs both trigger and data on that port; legal from JSON, not from the palette) |
| **Consumes** | `Vector<double>`, `Matrix<double>`, or `null` (stop/finalise); anything else → Stumbling |
| **Publishes** | nothing — results are read off `dB` : `List<(Vector<double> Target, Matrix<double> Data)>` |

## Parameters

None — `Params` is ignored.

## Requirements and use

Use capture start/stop controls to collect labelled segments before requesting batch training. A buffer accumulates data; it does not automatically train the downstream model.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.Buffer). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
