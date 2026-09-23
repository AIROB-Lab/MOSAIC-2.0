# Classifier

[Block Catalogue](../block-catalogue.md) / Machine Learning

Multi-class gesture classifier with two training modes: batch retraining from a Trigger Buffer, or per-sample incremental updates driven live by a Trigger.

## Input and output

| Property | Value |
|---|---|
| **Type** | `classifier`, `classifierblock` |
| **Inputs** | 2; see usage below |
| **Consumes** | `Vector<double>` (Count == InputDim) on the feature edge; `int` segment count from a `TriggerBuffer`; `Vector<double>` (argmax = class) or `null` from a Trigger |
| **Publishes** | `Vector<double>` of class probabilities, length NumClasses |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | modelType | string enum | *required* | `Softmax` \| `SoftmaxRFF` \| `LDA` \| `KNN` \| `RandomForest` \| `LinearSVM` \| `Threshold`; unknown throws |
| 1 | inputDim | int | *required* | Feature vector length |
| 2 | numClasses | int | *required* | Initial class count |
| 3 | trainingMode | string | `Buffer` | `Buffer` or `Incremental`; unparseable falls back to `Buffer` silently |

`Params` null or Count < 3 throws. Indices 4+ are read by nothing.

## Requirements and use

Choose batch or incremental training and provide matching labels. Capture/train before interpreting predicted classes; use held-out observations to assess the result.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Example

[Train a simple classifier](../learning-lab.md) provides complete JSON for a one-feature,
two-class KNN example. Buffer mode retrains automatically when a completed segment count
increases; Force Retrain fits the stored data again. Reopening JSON does not restore
the fitted classifier or training segments.

## Implementation

[API reference](xref:MOSAIC.Models.MachineLearning.ClassifierBlock). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
