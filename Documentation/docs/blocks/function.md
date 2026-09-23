# Function

[Block Catalogue](../block-catalogue.md) / Signal Processing

Applies one element-wise mathematical map to every element of the stream — rectification, offset, scaling, power, clipping, or a soft threshold. The delegate is recompiled whenever the type or params change at runtime.

## Input and output

| Property | Value |
|---|---|
| **Type** | `function`, `mosaic.models.function` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>` or `Matrix<double>` |
| **Publishes** | same type and shape as the input, mapped element-wise |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | FunctionType | string | `abs` | abs \| add \| multiply \| power \| clip \| threshold (trimmed, lowercased) |
| 1 | Param1 | double | 0 | Operand for add/multiply/power/threshold; lower clip bound |
| 2 | Param2 | double | 0 | Upper clip bound only |

Numbers are parsed by a bespoke helper that tries InvariantCulture then CurrentCulture.

## Requirements and use

Choose an operation and its argument. For example, `multiply` with 2 doubles every value, while `power` with 2 squares it. Use the first-pipeline example to compare both.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Example

[Download a complete pipeline](../../examples/first-pipeline.json) and follow
[Your First Pipeline](../first-pipeline.md). Start the Clock to generate data.
The sine wave has amplitude 1 and frequency 1 Hz. Its square is nonnegative,
and its doubled branch has amplitude 2.

## Implementation

[API reference](xref:MOSAIC.Models.SignalProcessing.Function). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
