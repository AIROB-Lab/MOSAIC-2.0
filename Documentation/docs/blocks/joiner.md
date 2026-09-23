# Joiner

[Block Catalogue](../block-catalogue.md) / Flow Control

Fan-in: concatenates the most recent value from every input into one flat vector, emitted once per arrival from a designated timer input. This is how sensors at different rates get pinned to one fixed-layout feature vector.

## Input and output

| Property | Value |
|---|---|
| **Type** | `joiner`, `mosaic.models.joiner` |
| **Inputs** | 2 or more (max `int.MaxValue`); declared order **is** the output layout — declared input *k* always occupies segment *k*. The timer must be one of the inputs. |
| **Consumes** | per input: `Vector<double>`, `Matrix<double>`, or legacy `ValueTuple<string,object>` (Item1 = source name). Other payloads are not even recorded. |
| **Publishes** | one `DenseVector<double>` per timer tick; length = Σ `vec.Count`, or `RowCount × ColumnCount` for matrices, flattened **row-major** |

## Parameters

not positional; scans every entry, and with several matching tokens the **last** one wins. Mandatory.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| — | `timerBlockName:<Name>` or `timer:<Name>` | string | none — required | Name of the input whose arrival triggers a publish |

## Requirements and use

Preserve input order: it determines output channel order. Choose the timer input deliberately. Joiner combines the most recent values; it does not establish synchronized acquisition across devices.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Example

[Data and time](../data-and-time.md#multiple-sources-and-missing-data) explains why
latest-value concatenation is not synchronized sampling. Record input streams separately
when you need to inspect their acquisition timing.

## Implementation

[API reference](xref:MOSAIC.Models.Joiner). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
