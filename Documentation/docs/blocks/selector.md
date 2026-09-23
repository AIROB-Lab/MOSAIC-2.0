# Selector

[Block Catalogue](../block-catalogue.md) / Flow Control

Unwraps a (label, payload) tuple and forwards only the payload, so downstream blocks expecting a bare Vector/Matrix can consume tagged output unmodified.

## Input and output

| Property | Value |
|---|---|
| **Type** | `selector`, `mosaic.models.flowcontrol.selector`, `imblocks.blocks.flowcontrol.selector` |
| **Inputs** | exactly 1 |
| **Consumes** | `ValueTuple<string,Vector<double>>`, `ValueTuple<string,Matrix<double>>`, `ValueTuple<string,object>` when Item2 is a Vector/Matrix, plus legacy `Tuple<…>` forms |
| **Publishes** | the unwrapped `Vector<double>` or `Matrix<double>` — Item2, verbatim |

## Parameters

None — `Params` is ignored.

## Requirements and use

Choose which input to forward. Ensure consumers support the selected source shape; switching sources does not convert their payloads.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.Selector). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
