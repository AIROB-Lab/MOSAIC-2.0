# Crop

[Block Catalogue](../block-catalogue.md) / Signal Processing

Slices a sub-range out of a vector or matrix — trimming an A-mode line to a region of interest, discarding leading metadata samples, or keeping a subset of channels.

## Input and output

| Property | Value |
|---|---|
| **Type** | `crop` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>` (depth line) or `Matrix<double>` [channels × depth samples] |
| **Publishes** | same type as the input: `v.SubVector(DepthIndex, DepthCount)` or the corresponding `m.SubMatrix(...)` |

## Parameters

two accepted shapes, gated on `Count`: the 2-param form, or the 4-param form. There is no 3-param form.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | DepthIndex | int | 0 | First column/element kept |
| 1 | DepthCount | int | 1 | Number of columns/elements kept |
| 2 | RowIndex | int | 0 | First row kept (requires `Count >= 4`) |
| 3 | RowCount | int | 0 | Number of rows kept; 0 disables row cropping |

## Requirements and use

Set start/length for the intended axis and verify the resulting dimensions. Cropping an image/depth axis is different from selecting vector channels.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.SignalProcessing.Crop). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
