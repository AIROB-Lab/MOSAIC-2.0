# Trigger Buffer

[Block Catalogue](../block-catalogue.md) / Flow Control

Captures labelled training segments, pairing every data frame with whichever target the trigger published most recently — one storage shape for both classification and regression.

## Input and output

| Property | Value |
|---|---|
| **Type** | `triggerbuffer`, `mosaic.models.triggerbuffer` |
| **Inputs** | 2; see usage below |
| **Consumes** | trigger side: `Vector<double>` (start segment / update target) or `null` (stop and finalise). Data side: `Matrix<double>` (row-block) or `Vector<double>` (wrapped as a 1-row matrix) |
| **Publishes** | an `int` — the new `dB.Count` — once per finalised segment; segments are read off `dB` : `List<(string Label, Matrix<double> Targets, Matrix<double> Data)>` |

## Parameters

None. Capture and target state are managed through its inputs and controls. The factory
attaches the common recording support, but the generic CSV dumper does not serialize
the integer segment-count notification. To record observations, enable recording on
the upstream data block; use the training/capture workflow for labelled segments.

## Requirements and use

Connect observation data and the matching trigger. Capture labelled segments before using a batch learner; check feature and target dimensions across all segments.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Example

[The learning experiment](../learning-lab.md) supplies the complete wiring and capture
steps. A finished five-second segment contains about 20 one-dimensional MAV observations.
Its Save to CSV export is separate from the generic Record switch.

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.TriggerBuffer). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
