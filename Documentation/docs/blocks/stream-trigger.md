# Stream Trigger

[Block Catalogue](../block-catalogue.md) / Flow Control

Proxies a continuously-varying Vector source (dataglove, BodyRig, SinGenerator) into a Trigger Buffer as per-frame regression targets, republishing whatever arrived most recently.

## Input and output

| Property | Value |
|---|---|
| **Type** | `streamtrigger`, `mosaic.models.streamtrigger` |
| **Inputs** | exactly 1 — a Vector-publishing upstream |
| **Consumes** | `Vector<double>` only; anything else returns immediately |
| **Publishes** | the same `Vector<double>`, but only while `IsStreaming`; plus a single `null` on `StopStream` to end the segment |

## Parameters

Use the session-name control for JSON-loaded pipelines. See the type-handling limitation below.

## Requirements and use

Use the session controls to coordinate stream capture. A programmatically supplied string at Params[0] can set SessionName; the current implementation checks for a CLR string, so do not rely on a JSON-deserialized string being applied. Set the name through the UI when loading JSON.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.StreamTrigger). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
