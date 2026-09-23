# Sin Generator

[Block Catalogue](../block-catalogue.md) / Streaming

Synthetic multi-channel sine source: on each clock tick it evaluates A·sin(2πft + φ₀ + i·Δφ) per channel, using a jitter-free internal sample counter rather than the incoming timestamp.

## Input and output

| Property | Value |
|---|---|
| **Type** | `singenerator`, `mosaic.models.singenerator` |
| **Inputs** | exactly 1, and whitelisted to `ClockBlock` — nothing else may feed it |
| **Consumes** | the clock's `double` tick; the value is ignored, it only triggers evaluation |
| **Publishes** | `Vector<double>` with one scan; `Matrix<double>` with multiple scans (rows = scans, columns = channels) |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | Components | int | 1 | Channel count (forced ≥1) |
| 1 | Amplitude | double | 1.0 | Peak amplitude |
| 2 | Frequency | double | 0.2 | Sine frequency in Hz; time base uses the true sample rate |
| 3 | Phase | double | 0.0 | Initial phase, **radians** |
| 4 | PhaseStep | double | 10.0 | Inter-channel phase offset, **radians** |
| 5 | ScansPerPacket | int | 1 | Samples per tick; values greater than 1 produce matrices |

## Requirements and use

Start its upstream Clock. ScansPerPacket = 1 publishes a vector; larger values publish matrices with rows = scans and columns = components. The true sample rate is tick rate × scans per packet. Five-parameter configurations remain valid and default to one scan.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Example

[Download a complete pipeline](../../examples/first-pipeline.json) and follow
[Your First Pipeline](../first-pipeline.md). Start the Clock to generate data.
The sine wave has amplitude 1 and frequency 1 Hz. Its square is nonnegative,
and its doubled branch has amplitude 2.

## Worked example

Compare the source in [Signal Processing Walkthrough](../signal-lab.md) with the
[batched source](../data-and-time.md#batches-are-not-necessarily-overlapping-windows):
256 one-row publications/s and 64 four-row packets/s both represent 256 samples/s.

## Implementation

[API reference](xref:MOSAIC.Models.Streaming.SinGenerator). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
