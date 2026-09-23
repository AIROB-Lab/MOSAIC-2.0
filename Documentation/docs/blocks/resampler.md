# Resampler

[Block Catalogue](../block-catalogue.md) / Signal Processing

Sample-and-hold rate converter. Selects or repeats values on a nominal sample-time grid.
Packet sizes and callback speed do not determine which values it selects. Its output
`DesiredRate` and `SignalRate` both equal `TargetRate`.

## Input and output

| Property | Value |
|---|---|
| **Type** | `resampler` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>`; `Matrix<double>` [timesteps × channels], rows fed through the timing logic individually; or the legacy `ValueTuple<string, object>`, unwrapped and re-dispatched |
| **Publishes** | always `Vector<double>` — never a matrix, so matrix input becomes a stream of vectors (a type change driven by the input, not by a parameter) |

## Parameters

**not positional.** The whole list is scanned and the first usable entry wins: a `targetRate:<n>` / `rate:<n>` tagged string, or a bare positive number (InvariantCulture). Fallback chain: parsed value → `DesiredRate` → 100.0.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| any | TargetRate | double | 100.0 | Publish rate in Hz. Zero or negative candidates are skipped, not rejected |

## Requirements and use

Input vectors use the source publication rate; contiguous matrix rows use its sample
rate. If the source has no rate, the constructor/configuration `DesiredRate` is the
input-rate fallback; if that is also absent, the target rate is assumed. Give the source
correct metadata before interpreting the output as a resampled signal.

At 2 input samples/s and 4 output samples/s, inputs `[10, 20, 30]` at times
`[0, 0.5, 1]` produce `[10, 10, 20, 20, 30]` at nominal times
`[0, 0.25, 0.5, 0.75, 1]`. The held value at 0.25 arrives together with the input at
0.5. The block has no independent timer and cannot continue publishing after input stops.

Apply an appropriate low-pass filter before downsampling. This block does not perform
anti-alias filtering, interpolate between values, detect missing packets, or synchronize
device timestamps. Every matrix row is treated as new input; overlapping windows would
replay old samples. The Scope uses the target cadence, while CSV timestamps still mark
actual publication time.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Example

For the distinction between sample rate and publication rate, read [Data and time](../data-and-time.md).
A resampler changes the sample stream by holding or selecting values; changing a plot's
refresh rate does not. Do not use it as a timestamp synchronizer between devices.

## Implementation

[API reference](xref:MOSAIC.Models.SignalProcessing.Resampler). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
