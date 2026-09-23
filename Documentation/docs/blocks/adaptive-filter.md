# Adaptive Filter

[Block Catalogue](../block-catalogue.md) / Signal Processing

First-order IIR low-pass whose cutoff is recomputed every sample as `fc = exp(Offset + Deriviate·|dx_filt| + Magnitude·|x|)`. Built for heteroscedastic signals such as EMG envelopes: it smooths hard when the signal is quiet and opens up on fast transients.

## Input and output

| Property | Value |
|---|---|
| **Type** | `adaptivefilterblock`, `adaptivefilter`, `mosaic.models.adaptivefilterblock` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>` (one sample, one element per channel) or `Matrix<double>` [timesteps × channels] |
| **Publishes** | same type and shape as the input (`Vector<double>` → `Vector<double>`, `Matrix<double>` → `Matrix<double>`) |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | Alpha | double | 5.0 | Derivative pre-filter smoothing on a UI scale 0–10; internally α = Alpha/10 |
| 1 | Offset | double | 0.0 | Base exponent |
| 2 | Magnitude | double | -20.0 | `\|x\|` contribution to the exponent |
| 3 | Deriviate | double | 10.0 | `\|dx_filt\|` contribution to the exponent |

All optional and cumulative; a shorter list takes the defaults. No param sets the sample rate — fs comes from `DesiredRate`, defaulting to 200 Hz.

## Requirements and use

Tune offset, derivative and magnitude terms for the units of your signal. Check the output against a known input before applying the filter to live control.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.AdaptiveFilterBlock). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
