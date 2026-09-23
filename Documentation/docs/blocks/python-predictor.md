# Python Predictor

[Block Catalogue](../block-catalogue.md) / Machine Learning

Classification delegated to a Python model over pythonnet — the block ships windows of data into a user-supplied Python `Model` class and republishes its probability output.

## Input and output

| Property | Value |
|---|---|
| **Type** | `pypredictor` |
| **Inputs** | 2; see usage below |
| **Consumes** | `Matrix<double>` only on the data edge (a `Vector<double>` is silently dropped); a `TriggerBuffer` reference on the training edge |
| **Publishes** | `Vector<double>` — `prediction[1]` from the Python tuple, float64 and flattened |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | modulePath | string | *required* | Python **module name** importable from `sys.path` — not a filesystem path |
| 1 | numClasses | int | *required* (1) | Class count passed to `Model(num_classes=)` |
| 2 | randomSeed | int | 42 | Passed to `Model(random_seed=)` |
| 3 | savePath | string | `""` | Passed to the Python side as `save_to` during training |

`Params` null or Count < 2 throws.

## Requirements and use

Configure the Python environment and a module implementing the expected `Model`
interface. Follow [Python Integrations](../python-integrations.md) for installation,
runtime variables, and the methods MOSAIC calls. Model-storage paths differ from CSV
destinations; use the Record control for shared-folder capture.

The public repository does not ship a classifier implementation, training data, or pretrained weights. Supply an independently licensed module through `modulePath`; keep generated model artifacts outside Git.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.MachineLearning.PyPredictor). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
