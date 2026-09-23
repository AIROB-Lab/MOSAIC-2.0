# Online PCA

[Block Catalogue](../block-catalogue.md) / Analytics

Streaming Principal Component Analysis — incrementally tracks the top k principal directions (Oja/Sanger update with periodic QR re-orthonormalisation) and projects each sample onto them.

## Input and output

| Property | Value |
|---|---|
| **Type** | `onlinepca`, `mosaic.models.onlinepca` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>` or `Matrix<double>` (row-wise) |
| **Publishes** | `Vector<double>` of length k, one per observation |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | k | int | 2 | Components; must be 2 or 3 |
| 1 | eta0 | double | 0.2 | Learning rate; decays as eta0/√n |
| 2 | reorthEvery | int | 100 | Re-orthonormalisation interval |
| 3 | minStableCount | int | 500 | Samples before IsStable flips |

## Requirements and use

Choose a component count that fits the feature dimension and allow adaptation to settle. Component directions can change as the online model learns.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Analytics.OnlinePCA). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
