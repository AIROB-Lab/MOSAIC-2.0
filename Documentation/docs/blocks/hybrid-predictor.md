# Hybrid Predictor

[Block Catalogue](../block-catalogue.md) / Machine Learning

Trains on discrete gesture labels like a classifier but outputs a continuous control vector — the probability-weighted blend of the per-class target vectors defined by the Trigger. Smooth prosthesis control from simple gesture labelling.

## Input and output

| Property | Value |
|---|---|
| **Type** | `hybridpredictor`, `hybridpredictorblock` |
| **Inputs** | 1–2; see usage below |
| **Consumes** | `Vector<double>` (Count == InputDim) on the feature edge; `Vector<double>` target or `null` from a Trigger |
| **Publishes** | `Vector<double>` = Σ_c p_c · targetVector_c |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | modelType | string enum | *required* | `Softmax` \| `SoftmaxRFF` \| `LDA` \| `KNN` \| `RandomForest` \| `LinearSVM` \| `Threshold`; unknown throws |
| 1 | inputDim | int | *required* | Feature vector length |
| 2 | learningRate | double | 0.01 | Incremental step size |
| 3 | lambda | double | 0.001 | Regularisation |
| 4 | sigma | double | 1.0 | RFF kernel width |
| 5 | featureDim | int | 300 | RFF feature count |

`Params` null or Count < 2 throws. There is deliberately no `numClasses` — classes are discovered from Trigger action names at runtime.

## Requirements and use

Train with discrete gesture labels and define the continuous target vector for each class. Predictions blend those target vectors according to class probabilities.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Learning.HybridPredictorBlock). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
