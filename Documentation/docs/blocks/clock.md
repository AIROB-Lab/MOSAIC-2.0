# Clock

[Block Catalogue](../block-catalogue.md) / Streaming

Master timing source: a dedicated highest-priority background thread publishes a monotonic timestamp at `DesiredRate` Hz, driving every synthetic pipeline.

## Input and output

| Property | Value |
|---|---|
| **Type** | `clockblock`, `mosaic.models.clockblock`, `clock` |
| **Inputs** | 0; see usage below |
| **Consumes** | nothing; `OnReceive` throws `NotImplementedException` if ever invoked |
| **Publishes** | `double` — monotonic seconds, one per tick |

## Parameters

None — `Params` is ignored. Rate comes from the JSON `DesiredRate` field (default 200), name from `Name` (default `Clock`).

## Requirements and use

Press Play on the card to start and pause/stop it there. Set the desired rate before starting; this source drives synthetic and other tick-based acquisition blocks.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Example

[Download a complete pipeline](../../examples/first-pipeline.json) and follow
[Your First Pipeline](../first-pipeline.md). Start the Clock to generate data.
The sine wave has amplitude 1 and frequency 1 Hz. Its square is nonnegative,
and its doubled branch has amplitude 2.

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.ClockBlock). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
