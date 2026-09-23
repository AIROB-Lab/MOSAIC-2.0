# Control Algorithm

[Block Catalogue](../block-catalogue.md) / Flow Control

Maps a prediction vector onto prosthesis degrees-of-actuation via a runtime-swappable `IControlAlgorithmStrategy`.

## Input and output

| Property | Value |
|---|---|
| **Type** | `controlalgorithm` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>`, or `ValueTuple<string,object>` whose `Item2` is a `Vector<double>`; anything else returns silently |
| **Publishes** | `Dictionary<DegreesOfActuation, double>`, values 0–100 |

## Parameters

not positional; the first token starting `algorithm:` wins (scan breaks on first match).

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| — | `algorithm:<Name>` | string | `DirectControl` | Strategy: `DirectControl`, `StepwiseControl`, `BidirectionalControl`, `DLControl` |

## Requirements and use

Choose a control strategy and map its incoming predictions to the required degrees of actuation. Check output ordering against the receiving hand, avatar or network block.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.FlowControl.ControlAlgorithm). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
