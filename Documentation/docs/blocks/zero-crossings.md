# Zero Crossings

[Block Catalogue](../block-catalogue.md) / Flow Control

Counts sign changes through zero per channel per window — a cheap proxy for dominant frequency content.

## Input and output

| Property | Value |
|---|---|
| **Type** | `zerocrossings`, `mosaic.models.flowcontrol.zerocrossings`, `imblocks.blocks.flowcontrol.zerocrossings` |
| **Inputs** | exactly 1 |
| **Consumes** | `Matrix<double>` only [samples × channels]; anything else → Stumbling |
| **Publishes** | `Vector<double>`, one count (as a double) per column |

## Parameters

positional.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | Threshold | double | 0.0 | Minimum \|current − next\| on the crossing pair. Read through the tolerant `JsonModel.GetDouble`: a wrong value kind silently yields 0.0, and a numeric string like `"0.01"` parses fine. |

## Requirements and use

Use windowed data. Noise and signal offset affect sign changes; condition the signal and keep the same window settings between training and prediction.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.ZeroCrossings). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
