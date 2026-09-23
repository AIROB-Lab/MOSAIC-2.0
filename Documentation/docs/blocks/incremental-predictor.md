# Incremental Predictor

[Block Catalogue](../block-catalogue.md) / Machine Learning

Online multi-output regression: updates on every sample while a Trigger holds a target vector, and predicts continuously otherwise. The standard proportional-control regressor.

## Input and output

| Property | Value |
|---|---|
| **Type** | `incrementalpredictor`, `incrementalpredictorblock` |
| **Inputs** | 2; see usage below |
| **Consumes** | `Vector<double>` (Count == InputDim) or `Matrix<double>` (ColumnCount == InputDim) on the feature edge; any `Vector<double>` (start) or `null` (stop) from a Trigger |
| **Publishes** | `Vector<double>`, length OutputDim (one per vector, one per matrix) |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | modelType | string enum | *required* | `Ridge` \| `RidgeRFF` \| `RecursiveLeastSquares`; unknown throws |
| 1 | inputDim | int | *required* | Feature vector length |
| 2 | outputDim | int | *required* | Target/prediction length |
| 3 | lambda | double | 1.0 | Regularisation |
| 4 | sigma | double | 1.0 | RFF kernel width (RFF only) |
| 5 | featureDim | int | 300 | RFF feature count (RFF only) |

`Params` null or Count < 3 throws.

## Requirements and use

Provide observations and matching targets during capture. Stop supervised updates when evaluating predictions so evaluation data does not become training data.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.MachineLearning.IncrementalPredictor). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
