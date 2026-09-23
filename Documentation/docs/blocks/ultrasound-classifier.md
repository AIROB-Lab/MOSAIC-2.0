# Ultrasound Classifier

[Block Catalogue](../block-catalogue.md) / Machine Learning

ResNet-18 over pythonnet: one B-mode ultrasound frame in, a softmax gesture-probability vector out. Can be flipped to a regression head producing independent sigmoid activations.

## Input and output

| Property | Value |
|---|---|
| **Type** | `usprediction` |
| **Inputs** | 2; see usage below |
| **Consumes** | `Matrix<double>` only, treated as one whole B-mode frame (a `Vector<double>` is silently dropped); a `TriggerBuffer` reference on the training edge |
| **Publishes** | `Vector<double>` of length numClasses — softmax probabilities, or independent sigmoid activations in regression mode |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | modulePath | string | `us_classifier` | Python module name |
| 1 | numClasses | int | 9 | Output length |
| 2 | inH | int | 224 | Frame height fed to the net |
| 3 | inW | int | 224 | Frame width fed to the net |
| 4 | learningRate | double | 1e-4 | Training step size |
| 5 | randomSeed | int | 42 | Seed |
| 6 | modelPath | string | `""` | state_dict checkpoint |
| 7 | isRegression | boolish | false | `bool` / 0-1 / `"true"` / `"yes"` / `"on"` |
| — | `regression` / `mode:regression` | sentinel | — | Any element equal to either also flips regression mode |

`Params` null or Count < 1 throws, even though every value has a default.

## Requirements and use

Requires the configured Python environment, a compatible PyTorch build, a user-supplied
model, and matching image preprocessing. Follow
[Python Integrations](../python-integrations.md) for runtime variables and the required
`Model` interface. Match frame dimensions and class ordering before using predictions
downstream.

The public repository intentionally excludes the experimental classifier module, training data, pretrained weights, and checkpoints. Provide independently licensed replacements and keep generated model files outside Git.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.MachineLearning.UltrasoundClassifier). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
