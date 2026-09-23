# Manual Control

[Block Catalogue](../block-catalogue.md) / Flow Control

UI sliders as a degrees-of-actuation source. Emits the same dictionary shape as Control Algorithm, so it drops straight in front of a sender to hand-drive the hand.

## Input and output

| Property | Value |
|---|---|
| **Type** | `manualcontrol` |
| **Inputs** | exactly 1 — a periodic publisher, normally a Clock |
| **Consumes** | anything; the value is ignored, only the tick matters |
| **Publishes** | `Dictionary<DegreesOfActuation, double>`, values 0–100 |

## Parameters

None — `Params` is ignored.

## Requirements and use

Adjust the card sliders to generate control targets without a predictor. Match the target degrees of actuation to the downstream control or device block.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.ManualControl). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
