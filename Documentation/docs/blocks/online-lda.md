# Online LDA

[Block Catalogue](../block-catalogue.md) / Analytics

Streaming Linear Discriminant Analysis — maintains per-class means and within/between-class scatter online, projecting into a k-dim subspace that maximises class separation. Feeds the labelled scatter-plot UI.

## Input and output

| Property | Value |
|---|---|
| **Type** | `onlinelda`, `mosaic.models.onlinelda` |
| **Inputs** | 2; see usage below |
| **Consumes** | `Vector<double>` or `Matrix<double>` (row-wise) on the data edge; `Vector<double>` (start) or `null` (stop) on the label edge |
| **Publishes** | `ValueTuple<string, Vector<double>>` — (label, k-dim projection) |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | k | int | 2 | Components; must be 2 or 3 |
| 1 | eta0 | double | 0.1 | Learning rate |
| 2 | reorthEvery | int | 100 | Re-orthonormalisation interval |
| 3 | minStableCount | int | 500 | Samples before IsStable flips |
| 4 | regularization | double | 1e-4 | Added to the Sw diagonal |

## Requirements and use

Provide labelled observations and enough classes/data before interpreting the projection. Keep the label path distinct from the numerical feature dimensions.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Analytics.OnlineLDA). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
