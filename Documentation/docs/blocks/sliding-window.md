# Sliding Window

[Block Catalogue](../block-catalogue.md) / Flow Control

Accumulates incoming rows and publishes a BufferSize × Channels matrix every Stride rows. Windows overlap when Stride is smaller than BufferSize; setting them equal produces non-overlapping windows.

## Input and output

| Property | Value |
|---|---|
| **Type** | `slidingwindow`, `mosaic.models.slidingwindow` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>` (one row), `Matrix<double>` (batch of rows, appended in order), or `ValueTuple<string,object>` wrapping either; anything else is silently dropped |
| **Publishes** | `Matrix<double>` [BufferSize × Channels] with window coefficients already applied element-wise |

## Parameters

positional.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | BufferSize | int | 100 | Rows per published window; must be > 0 |
| 1 | Stride | int | 25 | Rows between publishes; must be > 0 and ≤ BufferSize |
| 2 | WindowType | string | `"Hamming"` | `Rectangular`, `Hamming`, or `Hann`, parsed case-insensitively |

## Requirements and use

Choose **BufferSize** for history length and **Stride** for update spacing. At 256
samples/s, BufferSize 256 means one second of history. Stride 64 yields four windows/s
whether samples arrive as individual vectors or contiguous packets; stride 256 yields
one non-overlapping window/s. Constructor, JSON loader and palette share these defaults.

The first window publishes immediately after padding BufferSize - 1 rows with zeros.
Early features and spectra therefore include startup padding. WindowType weights
each row before publication. For continuous filtering, place Filter upstream.
For FFT, choose a power-of-two BufferSize; the current FFT rejects other row counts.
Apply a taper in one place instead of inadvertently weighting both here and in FFT.

The block's own scope receives incoming samples, not its output windows. A downstream
feature scope displays one feature per window. A downstream scope receiving complete
windows needs an explicit policy for retained rows. See [Reading the plots](../reading-plots.md).

For contiguous matrix packets, the window cadence is the upstream sample rate divided
by stride. For vectors (including feature vectors), it is the upstream publication rate
divided by stride. Do not chain overlapping windows as though all their rows were new
samples. See [Data and time](../data-and-time.md).

## Example

[Signal Processing Walkthrough](../signal-lab.md) provides a complete six-block download with explicit
256-row, stride-64 rectangular windows, expected features, FFT results and CSV interpretation.
Use it to compare overlap and non-overlap without changing the acquisition rate.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.SignalProcessing.SlidingWindow). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
