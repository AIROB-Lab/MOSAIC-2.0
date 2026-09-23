# Python Predictor (Regression)

[Block Catalogue](../block-catalogue.md) / Machine Learning

Continuous multi-output regression delegated to a PyTorch model over pythonnet — windows of multichannel EMG in, a smoothed joint/DOF activation vector out.

## Input and output

| Property | Value |
|---|---|
| **Type** | `pypredictorregression` |
| **Inputs** | 2; see usage below |
| **Consumes** | `Matrix<double>` only on the data edge (a `Vector<double>` is silently dropped); a `TriggerBuffer` reference on the training edge |
| **Publishes** | `Vector<double>`, length numOutputs (float64, flattened) |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | modulePath | string | *required* | Python module name |
| 1 | inChannels | int | 32 | Input channel count |
| 2 | winLen | int | 200 | Window length in samples |
| 3 | fs | int | 2000 | Sample rate |
| 4 | randomSeed | int | 42 | Passed to the model |
| 5 | modelPath | string | `""` | `.pt`/`.pth` state_dict |
| 6 | stateDictPath | string | `""` | Reserved compatibility field; currently ignored |
| 7 | numOutputs | int | 8 | Output vector length |
| — | `preprocess:skip` | sentinel | — | Any element whose string form equals this (case-insensitive) sets skipPreprocess |

`Params` null or Count < 1 throws.

## Requirements and use

Install the Python environment and a compatible PyTorch build, then select a model
with matching input and target dimensions. Follow
[Python Integrations](../python-integrations.md) for runtime variables and the
required `Model` interface. Model persistence uses its own path semantics; do not
replace a model path with a CSV folder.

No regression module, training data, pretrained weights, or checkpoints are distributed in the public repository. Supply independently licensed artifacts and keep generated model files outside Git.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.MachineLearning.PyPredictorRegression). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
