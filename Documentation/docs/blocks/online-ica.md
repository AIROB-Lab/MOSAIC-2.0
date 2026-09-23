# Online ICA

[Block Catalogue](../block-catalogue.md) / Analytics

Streaming Independent Component Analysis — incrementally learns an unmixing matrix that separates mixed channels (EMG crosstalk, motor-unit sources) into k independent components.

## Input and output

| Property | Value |
|---|---|
| **Type** | `onlineica`, `mosaic.models.onlineica` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>` (one observation) or `Matrix<double>` (each row an independent observation) |
| **Publishes** | `Vector<double>` of length k, one per observation |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | k | int | 2 | Component count |
| 1 | eta0 | double | 0.1 | Learning rate |
| 2 | reorthEvery | int | 50 | Re-orthonormalisation interval (samples) |
| 3 | minStableCount | int | 1000 | Samples before IsStable flips |
| 4 | contrastFunction | double | 0.0 | 0 = LogCosh, 1 = Exp, 2 = Cube |
| 5 | warmupSamples | int | 500 | Whitening warm-up; 0 disables |
| 6 | freezeWhiteningAfterWarmup | int (0/1) | 1 | Freeze whitening once warm |
| 7 | adaptiveWhitening | int (0/1) | 0 | Keep adapting the whitener |
| 8 | covarianceDecay | double | 0.005 | Clamped to 0.0001–0.5 |

## Requirements and use

Allow the whitening/warm-up phase to complete before interpreting components. Component count must fit the observation dimension. The final three model parameters may need JSON configuration when absent from the palette.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Analytics.OnlineICA). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
