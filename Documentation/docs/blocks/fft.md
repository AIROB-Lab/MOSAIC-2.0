# FFT

[Block Catalogue](../block-catalogue.md) / Signal Processing

Single-sided FFT spectrum per channel from a windowed matrix, scaled as magnitude, normalized amplitude, power, or dB, feeding a live SpectrogramMonitor.

## Input and output

| Property | Value |
|---|---|
| **Type** | `fft` |
| **Inputs** | exactly 1; normally fed by a Sliding Window |
| **Consumes** | `Matrix<double>` [samples × channels], or `Vector<double>` (promoted to a single-column matrix) |
| **Publishes** | `Matrix<double>` with N/2 + 1 rows (frequency bins) × one column per channel — a shape change, not a type change |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | WindowType | string | `Rectangular` | Rectangular \| Hann \| Hamming \| Blackman |
| 1 | OutputMode | string | `Magnitude` | Magnitude \| MagnitudeNormalized \| Power \| DB |
| 2 | DbRef | double | 1.0 | dB reference; `<= 0` silently coerced to 1.0 |
| 3 | DbFloor | double | -120.0 | dB floor |
| 4 | SpectrogramDepth | int | 200 | Spectrogram history; clamped to >= 50 |
| 5 | SampleRate | double | 0.0 | Fixed frequency-axis override, consulted **only** when the pipeline supplies no SignalRate |

## Requirements and use

Feed windowed samples with the correct signal sample rate. Choose scaling and inspect frequency bins; the number of published spectra per second is not the sampling frequency of the waveform.

The row count must be a power of two: 128, 256, 512, and so on. Invalid sizes produce
no spectrum. At 256 samples/s with 256 rows, bin spacing is 1 Hz and the output has
129 rows, including DC and Nyquist. `MagnitudeNormalized` scales a bin-centred sine
toward its amplitude; raw Magnitude, Power and DB have different units/scales.
Use a rectangular upstream window if applying Hann or another taper here, so the
signal is not accidentally weighted twice.

## Example

[Signal Processing Walkthrough](../signal-lab.md) supplies an 8 Hz sine and checks for a peak at bin 8,
normalized amplitude near 1. Its complete JSON includes Filter and Sliding Window.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.SignalProcessing.FFT). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
