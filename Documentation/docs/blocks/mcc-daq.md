# MCC DAQ

[Block Catalogue](../block-catalogue.md) / Devices

Acquires analogue inputs through the Measurement Computing Universal Library.

## Input and output

| Property | Value |
|---|---|
| **Type** | `mccdaq`, `mc_daq`, `mcdaq` |
| **Inputs** | 1; see usage below |
| **Consumes** | clock ticks |
| **Publishes** | `Vector<double>` for one scan; `Matrix<double>` for multiple scans |

## Parameters

| Index | Parameter | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | Channels | string | `0;1;2;3;4;5;6;7` | Semicolon-separated selection |
| 1 | ScansPerPacket | int | 10 | At least one scan per tick |
| 2 | BoardNumber | int | 0 | Configured board index |
| 3 | Range | string | `Bip10Volts` | Device voltage range |
| 4 | Scale | string | `volts` | `volts` or `legacy` |

## Requirements and use

Requires the official MCC DAQ Software/Universal Library on Windows. The public
repository does not contain `MccDaq.dll`; supply it and build with
`-p:EnableMccDaq=true` as described in
[Packages and optional devices](../getting-started.md#enable-mcc-daq). Without that
opt-in build, the block remains visible but reports that the Universal Library is not installed.

Configure the board with InstaCal first, then start the Clock. The device scan rate
is tick rate × scans per packet. Match downstream processing to the selected batch shape.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Devices.MccDaqBoard). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
