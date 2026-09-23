# Slope Sign Changes

[Block Catalogue](../block-catalogue.md) / Flow Control

Counts slope sign reversals per channel per window — a standard EMG time-domain feature.

## Input and output

| Property | Value |
|---|---|
| **Type** | `slopesignchanges`, `mosaic.models.flowcontrol.slopesignchanges`, `imblocks.blocks.flowcontrol.slopesignchanges` |
| **Inputs** | exactly 1 |
| **Consumes** | `Matrix<double>` only [samples × channels]; anything else → Stumbling |
| **Publishes** | `Vector<double>`, one count (as a double) per column |

## Parameters

positional.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | Threshold | double | 0.0 | Minimum slope magnitude; **both** adjacent slopes must clear it. 0 counts every sign change. |

## Requirements and use

Use windowed data and choose the threshold for the units/noise level of the signal. The output is a per-channel feature, not a time-series reconstruction.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.SlopeSignChanges). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
