# Hannes Hand

[Block Catalogue](../block-catalogue.md) / Devices

Sink driving the Hannes prosthetic hand over a BLE serial dongle using the EMGEM protocol. Converts a 4-DOF normalised reference vector into a `SimultRefControl` packet and republishes the references.

## Input and output

| Property | Value |
|---|---|
| **Type** | `hanneshand`, `mosaic.models.hanneshand` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>` of 4 elements in 0..1 as `[hand, wristFE, wristPS, thumb]`; or `Dictionary<DegreesOfActuation, double>` on a 0..100 scale; or a `ValueTuple<string, object>` wrapping either. Anything else is logged and dropped |
| **Publishes** | `Vector<double>` — the resolved 4-element reference, republished verbatim |

## Parameters

None — `Params` is ignored. Only `Name`, `DesiredRate` and `Path` are read; port, BLE device and the per-DOF enable flags are card-only and not persisted.

## Requirements and use

Requires the Hannes hardware and compatible serial/BLE dongle. Select and connect the device on the card before sending control values. Match the incoming degrees of actuation to the hand configuration.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Devices.HannesHand). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
