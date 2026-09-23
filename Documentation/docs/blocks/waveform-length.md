# Waveform Length

[Block Catalogue](../block-catalogue.md) / Flow Control

Fixed single-feature extractor: WL = Σ|x[n] − x[n−1]| per channel per window, capturing amplitude and frequency content together.

## Input and output

| Property | Value |
|---|---|
| **Type** | `waveformlength`, `mosaic.models.flowcontrol.waveformlength`, `imblocks.blocks.flowcontrol.waveformlength` |
| **Inputs** | exactly 1 |
| **Consumes** | `Matrix<double>` only [samples × channels]; a `Vector<double>` sets Stumbling and is dropped |
| **Publishes** | `Vector<double>`, one WL value per column |

## Parameters

None — `Params` is ignored.

## Requirements and use

Use a Sliding Window upstream. The result sums successive absolute differences per channel and depends on both window length and sampling rate.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.WaveformLength). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
