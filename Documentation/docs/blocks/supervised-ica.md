# Supervised ICA

[Block Catalogue](../block-catalogue.md) / Analytics

Online ICA subclass that additionally stashes each projection into labelled clusters for the scatter-plot card. The ICA maths is unchanged.

## Input and output

| Property | Value |
|---|---|
| **Type** | `supervisedica`, `mosaic.models.supervisedica` |
| **Inputs** | 1–2; see usage below |
| **Consumes** | `Vector<double>` or `Matrix<double>` (row-wise), from every connected input indiscriminately |
| **Publishes** | `Vector<double>` of length k |

## Parameters

identical 9-slot layout to Online ICA:

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | k | int | 2 | Component count |
| 1 | eta0 | double | 0.1 | Learning rate |
| 2 | reorthEvery | int | 50 | Re-orthonormalisation interval |
| 3 | minStableCount | int | 1000 | Samples before IsStable flips |
| 4 | contrastFunction | double | 0.0 | 0 = LogCosh, 1 = Exp, 2 = Cube |
| 5 | warmupSamples | int | 500 | Whitening warm-up; 0 disables |
| 6 | freezeWhiteningAfterWarmup | int (0/1) | 1 | Freeze whitening once warm |
| 7 | adaptiveWhitening | int (0/1) | 0 | Keep adapting the whitener |
| 8 | covarianceDecay | double | 0.005 | Clamped to 0.0001–0.5 |

## Requirements and use

Capture labelled clusters through the learning controls. Do not assume that connecting a Trigger as an additional observation input automatically supplies labels correctly.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Analytics.SupervisedICA). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
