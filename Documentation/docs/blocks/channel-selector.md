# Channel Selector

[Block Catalogue](../block-catalogue.md) / Flow Control

Per-channel enable mask driven from the UI. Disabled channels are either zeroed in place or removed from the output entirely.

## Input and output

| Property | Value |
|---|---|
| **Type** | `channelselector`, `mosaic.models.flowcontrol.channelselector`, `imblocks.blocks.flowcontrol.channelselector` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>` (element = channel) or `Matrix<double>` [timesteps × channels] (column = channel) |
| **Publishes** | same kind as received — `Vector<double>` → `Vector<double>`, `Matrix<double>` → `Matrix<double>` |

## Parameters

None — `Params` is ignored. The mask is runtime-only (`UpdateChannelActivations` / `SetAllChannels` / `ToggleChannel` / `SetChannel`).

## Requirements and use

Select the channels to retain in the card. Downstream dimensions depend on that selection, so update consumers when changing the mask.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.ChannelSelector). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
