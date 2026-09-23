# Projector

[Block Catalogue](../block-catalogue.md) / Flow Control

Keeps only the listed channel indices, producing a reduced-dimension output.

## Input and output

| Property | Value |
|---|---|
| **Type** | `projector`, `mosaic.models.flowcontrol.projector`, `imblocks.blocks.flowcontrol.projector` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>` (selects elements) or `Matrix<double>` [timesteps × channels] (selects columns, all rows preserved) |
| **Publishes** | shorter `Vector<double>` / narrower `Matrix<double>`, same kind as the input |

## Parameters

positional.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | ChannelIndices | string | empty (catalog offers `"0;1;2"`) | Semicolon-separated zero-based indices, e.g. `"0;2;4;6"` |

## Requirements and use

Indices select channels in the order listed. Check them against the incoming vector length; repeated or reordered indices change the downstream feature layout.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.Projector). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
