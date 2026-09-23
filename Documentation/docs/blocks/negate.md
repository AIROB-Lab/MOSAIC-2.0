# Negate

[Block Catalogue](../block-catalogue.md) / Signal Processing

Flips the sign of each channel in a vector.

## Input and output

| Property | Value |
|---|---|
| **Type** | `negate` |
| **Inputs** | one |
| **Consumes** | `Vector<double>` |
| **Publishes** | a new `Vector<double>` of the same length |

## Parameters

None.

## Requirements and use

No dedicated card is required. For input `[1, -2, 0]`, expect `[-1, 2, 0]`. Unsupported payloads are not published. Use the first-pipeline tutorial to add it to a generated signal.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Example

[Download a complete pipeline](../../examples/negate.json) and follow
[Your First Pipeline](../first-pipeline.md). Start the Clock to generate data.
The inverted output is the negative of the sine signal; record it to compare values.

## Implementation

[API reference](xref:MOSAIC.Models.SignalProcessing.Negate). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
