# Supervised PCA

[Block Catalogue](../block-catalogue.md) / Analytics

Online PCA subclass that additionally stashes each projection into a colour-coded, labelled cluster set for the scatter-plot card. The PCA maths is unchanged.

## Input and output

| Property | Value |
|---|---|
| **Type** | `supervisedpca`, `mosaic.models.supervisedpca` |
| **Inputs** | exactly 1 — no label input is declared |
| **Consumes** | `Vector<double>` or `Matrix<double>` (row-wise) |
| **Publishes** | `Vector<double>` of length k |

## Parameters

identical to Online PCA:

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | k | int | 2 | Components; must be 2 or 3 |
| 1 | eta0 | double | 0.2 | Learning rate |
| 2 | reorthEvery | int | 100 | Re-orthonormalisation interval |
| 3 | minStableCount | int | 500 | Samples before IsStable flips |

## Requirements and use

Capture the intended classes through the card controls, then inspect the learned projection. Keep observation dimensions fixed across captures.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Analytics.SupervisedPCA). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
