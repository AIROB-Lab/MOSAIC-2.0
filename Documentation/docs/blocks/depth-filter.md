# Depth Filter

[Block Catalogue](../block-catalogue.md) / Signal Processing

The same FIR/IIR designs as Filter, but applied **along each row** (the depth axis) with the state reset before every row, so frames are independent. Built for A-mode ultrasound lines and similar spatial signals.

## Input and output

| Property | Value |
|---|---|
| **Type** | `depthfilter` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>` (one depth line) or `Matrix<double>` [channels × depth samples] |
| **Publishes** | `Vector<double>` or `Matrix<double>` of identical shape; each row filtered independently. Also feeds a HeatMapMonitor and a SnapshotMonitor |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | FilterType | string | `Lowpass` | Lowpass \| Highpass \| Bandpass \| Bandstop |
| 1 | Implementation | string | `IIR` | IIR \| FIR |
| 2 | CutoffLow | double | 50 | Lower cutoff (Hz, against DepthSampleRate) |
| 3 | CutoffHigh | double | 150 | Upper cutoff; used only for Bandpass/Bandstop, always read |
| 4 | Order | int | 4 | IIR order |
| 5 | FirTaps | int | 64 | FIR tap count |
| 6 | DepthSampleRate | double | 1000 | The **only** thing that sets the design rate |

All optional, positional, cumulative.

## Requirements and use

Filters along each row with independent frame processing. Use it for a depth/spatial axis, not as a substitute for a time-domain filter that carries state between samples.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.SignalProcessing.DepthFilter). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
