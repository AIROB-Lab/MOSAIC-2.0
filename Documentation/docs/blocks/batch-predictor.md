# Batch Predictor

[Block Catalogue](../block-catalogue.md) / Machine Learning

Multi-output regression fit in one shot over every labelled segment held by an upstream Trigger Buffer, then predicting continuously. The batch counterpart to Incremental Predictor.

## Input and output

| Property | Value |
|---|---|
| **Type** | `batchpredictor`, `batchpredictorblock` |
| **Inputs** | declared exactly 1; functionally needs 2 (feature source + `TriggerBuffer`) |
| **Consumes** | `Vector<double>` (Count == InputDim) or `Matrix<double>` (ColumnCount == InputDim) on the feature edge; a `TriggerBuffer` reference on the training edge |
| **Publishes** | `Vector<double>`, length OutputDim (one per vector; for a matrix, the last row only) |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | modelType | string enum | *required* | `Ridge` \| `RidgeRFF` \| `RecursiveLeastSquares`; unknown throws |
| 1 | inputDim | int | *required* | Feature vector length |
| 2 | outputDim | int | *required* | Prediction length |
| 3 | lambda | double | 1.0 | Regularisation |
| 4 | sigma | double | 1.0 | RFF kernel width (RFF only) |
| 5 | featureDim | int | 300 | RFF feature count (RFF only) |

`Params` null or Count < 3 throws.

## Requirements and use

Capture labelled segments in a Trigger Buffer, fit the model, then inspect predictions on new data. Keep feature ordering and target dimensions consistent.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Example

For a complete capture/training introduction, use the
[Classifier walkthrough](../learning-lab.md). It uses a different learning block.
For this regression block, provide both the feature source and TriggerBuffer in JSON;
the one-input editor constraint is a current limitation, so do not infer the working
training topology from the declared count alone.

## Implementation

[API reference](xref:MOSAIC.Models.MachineLearning.BatchPredictor). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
