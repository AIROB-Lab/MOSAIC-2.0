# Switch

[Block Catalogue](../block-catalogue.md) / Flow Control

A/B selector — forwards whatever the currently active one of two upstreams publishes and drops the other.

## Input and output

| Property | Value |
|---|---|
| **Type** | `switch`, `mosaic.models.flowcontrol.switch`, `imblocks.blocks.flowcontrol.switch` |
| **Inputs** | 2; see usage below |
| **Consumes** | any `object` — the payload is never inspected |
| **Publishes** | exactly the object received, unchanged; output type mirrors whichever input is active |

## Parameters

None — `Params` is ignored. Active input is set at runtime via `Toggle()` / `SetActiveInput(int)` / `UseSecondInput`.

## Requirements and use

Connect two compatible sources and choose the active input. Inactive input values are discarded rather than buffered for later playback.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.Switch). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
