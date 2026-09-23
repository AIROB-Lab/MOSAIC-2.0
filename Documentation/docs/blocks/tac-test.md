# TAC Test

[Block Catalogue](../block-catalogue.md) / Tests

Target Achievement Control test (Simon et al. 2011): scores how well a user drives a predictor onto Stimulus-supplied target postures, tracking L2 distance, dwell time, overshoots, path efficiency and completion rate per trial.

## Input and output

| Property | Value |
|---|---|
| **Type** | `tac` |
| **Inputs** | 2; see usage below |
| **Consumes** | `ValueTuple<string, Vector<double>>` (state, target) or bare `Vector<double>` from Stimulus; `Vector<double>` or `ValueTuple<string, Vector<double>>` (Item2 taken) from the predictor |
| **Publishes** | `double` — the current L2 distance between prediction and target, once per evaluation tick |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | stimulusBlockName | string | `Stimulus` | Surfaced to `TACViewModel`; selects nothing |
| 1 | dwellTime | double | 2.0 | Seconds inside the target to count as achieved |
| 2 | successThreshold | double | 0.2 | Max L2 distance counted as in-target |

## Requirements and use

Set target geometry, timing and control mappings for the test. Verify the control vector and coordinate conventions before beginning a scored run.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Tests.TAC). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
