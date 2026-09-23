# Supervised UMAP

[Block Catalogue](../block-catalogue.md) / Analytics

Label-guided non-linear dimensionality reduction via umap-learn over pythonnet. Captures labelled feature clusters, fits a supervised embedding on demand, then transforms live samples into it.

## Input and output

| Property | Value |
|---|---|
| **Type** | `supervisedumap`, `umap` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>` or `Matrix<double>` (row-wise) |
| **Publishes** | `Vector<double>` of length nComponents — only once IsFitted is true and IsFitting is false |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | nComponents | int | 3 | Embedding dimension; must be 2 or 3 |
| 1 | nNeighbors | int | 15 | UMAP neighbourhood size |
| 2 | minDist | double | 0.1 | UMAP minimum distance |
| 3 | randomState | int | 42 | Seed |

The `Metric` property (`euclidean`) is UI-bindable but not read from JSON.

## Requirements and use

Install and configure the retained general Python environment as described in
[Python Integrations](../python-integrations.md). Capture clusters and fit the model
before expecting transformed output; check initialization and fit messages if no
output appears. Load only trusted saved UMAP models because their pickle format can
execute code during loading.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Analytics.SupervisedUMAP). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
