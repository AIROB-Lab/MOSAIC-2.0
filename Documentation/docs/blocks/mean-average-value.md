# Mean Average Value

[Block Catalogue](../block-catalogue.md) / Flow Control

Fixed single-feature extractor: MAV = (1/N)·Σ|x[i]| per channel over a window. For switchable metrics use the Analytics Metrics Extractor.

## Input and output

| Property | Value |
|---|---|
| **Type** | `meanaveragevalue`, `mosaic.models.flowcontrol.meanaveragevalue`, `imblocks.blocks.flowcontrol.meanaveragevalue` |
| **Inputs** | exactly 1 |
| **Consumes** | `Matrix<double>` only [samples × channels]; a `Vector<double>` sets Stumbling and is dropped |
| **Publishes** | `Vector<double>`, one MAV per column |

## Parameters

None — `Params` is ignored.

## Requirements and use

Place a Sliding Window upstream. Each output contains one mean absolute value per channel, so window size controls smoothing and response time.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Example

Use [Signal Processing Walkthrough](../signal-lab.md): one channel at 256 samples/s, 256-row windows,
stride 64. Expect one feature near 0.63 at four updates/s for an amplitude-1 sine.
[Train a simple classifier](../learning-lab.md) uses that same feature to distinguish amplitudes.

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.MeanAverageValue). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
