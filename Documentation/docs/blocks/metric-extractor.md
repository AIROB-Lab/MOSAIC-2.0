# Metric Extractor

[Block Catalogue](../block-catalogue.md) / Analytics

Collapses a windowed matrix to one scalar amplitude/feature value per channel. The standard feature stage between a Sliding Window and a predictor.

## Input and output

| Property | Value |
|---|---|
| **Type** | `metricextractor` |
| **Inputs** | exactly 1 |
| **Consumes** | `Matrix<double>` [samples × channels] |
| **Publishes** | `Vector<double>`, length = input ColumnCount (one per channel) |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | Metric | string enum | `RMS` | `RMS` \| `MAV` \| `IEMG` \| `SSI` \| `VAR` \| `STD` \| `LOG` \| `PEAK` \| `P2P` \| `MEAN` \| `ZSCORE`; case-insensitive |

Indices 1+ are ignored.

## Requirements and use

Supply a windowed matrix and select the metric name. Window length and overlap belong to the upstream Sliding Window. Keep the same feature definition for training and inference.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Analytics.MetricsExtractor). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
