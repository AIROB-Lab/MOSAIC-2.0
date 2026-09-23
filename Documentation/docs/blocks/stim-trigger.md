# Stim Trigger

[Block Catalogue](../block-catalogue.md) / Flow Control

Adapter converting a Stimulus block's FSM state tuples into the Buffer start/stop protocol, so a scripted stimulus schedule can drive capture without user clicks.

## Input and output

| Property | Value |
|---|---|
| **Type** | `stimtrigger`, `mosaic.models.flowcontrol.stimtrigger`, `imblocks.blocks.flowcontrol.stimtrigger` |
| **Inputs** | exactly 1, hard-enforced (`m.Inputs.Count != 1` throws); must be a Stimulus block |
| **Consumes** | `ValueTuple<string, Vector<double>>` — (FSM state name, target vector) |
| **Publishes** | `Vector<double>` (the target) while state == `"capture"`; a single `null` on the first non-capture state seen while capturing; nothing otherwise |

## Parameters

None — `Params` is ignored. Note `DesiredRate` defaults to **200 Hz** when absent, not 0.

## Requirements and use

Use with a compatible stimulus sequence. Align labels and target dimensions with the capture/training block that receives its output.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.StimTrigger). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
