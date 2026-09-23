# Trigger

[Block Catalogue](../block-catalogue.md) / Flow Control

Manual label source for supervised training. Holds named activation vectors; StartCapture publishes the selected one, StopCapture publishes null. Oscillating mode sweeps the vector with a sin² envelope for regression training.

## Input and output

| Property | Value |
|---|---|
| **Type** | `trigger`, `mosaic.models.flowcontrol.trigger`, `imblocks.blocks.flowcontrol.trigger` |
| **Inputs** | 0; see usage below |
| **Consumes** | nothing — `OnReceive` is a no-op |
| **Publishes** | `Vector<double>` — the action vector, or `baseVec·sin²(2π·f·t)` in oscillating mode — and `null` on StopCapture |

## Parameters

variadic list of strings, at least 1 required. Config directives are checked first; anything else is parsed as an action.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0..n | `<name>:v1;v2;v3` | string | catalog offers `"class1:1.0;2.0"` | Named action vector; values parsed with `double.Parse`, InvariantCulture |
| 0..n | `mode:<m>` | string | classic | `oscillating`\|`oscillate`\|`sine` or `classic`\|`constant`\|`static` |
| 0..n | `freq:<Hz>` / `frequency:<Hz>` | double | 0.5 | Oscillation frequency; must be > 0 |
| 0..n | `tickrate:<Hz>` / `tick:<Hz>` | double | 30.0 | Oscillation republish rate; must be > 0 |

## Requirements and use

Define a target/action before starting capture, then stop capture to close the segment. Trigger output is control information, not another observation vector to feed blindly into a signal processor.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Example

[Train a simple classifier](../learning-lab.md) configures Low and High one-hot targets,
uses Classic capture, and explains when to wait, start and stop. Labels are control data,
not additional observation channels.

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.Trigger). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
