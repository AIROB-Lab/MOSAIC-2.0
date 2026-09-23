# WULPUS

[Block Catalogue](../block-catalogue.md) / Devices

Bridges the Wulpus wearable ultra-low-power ultrasound dongle into the pipeline through its Python package (pythonnet), publishing A-mode acquisitions or assembled B-mode frames.

## Input and output

| Property | Value |
|---|---|
| **Type** | `wulpus`, `wulpuspython` |
| **Inputs** | 1 in the current graph editor and loader |
| **Consumes** | A timer tick; its payload is ignored. Ticks received before streaming starts are dropped. |
| **Publishes** | mode 0: `Vector<double>` of `NumSamples` (+2). Modes 1 and 2: `Matrix<double>` of `[NumChannelConfigs × NumSamples (+2)]`. The 2 extra trailing values are the config index and acquisition counter |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | ussConfigPath | string | `""` | USS config file |
| 1 | rxTxConfigPath | string | `""` | RX/TX config file |
| 2 | DataSendingMode | int | 0 | 0 = SingleAcquisitionVector, 1 = CompleteFrameMatrix, 2 = UpdateSingleAcqMatrix |
| 3 | appendMetadata | bool | true | Keeps the 2 trailing metadata values; `CropMetadata` strips them when false |

## Requirements and use

Requires the retained Python 3.9 Wulpus environment and the hardware connection.
Follow [Python Integrations](../python-integrations.md) and run WULPUS in a separate
MOSAIC process from the Python 3.12 integrations. Select the acquisition mode first;
A-mode data and assembled B-mode frames have different interpretations.

The model contains an experimental self-triggered path for JSON with no `Inputs`, but
the current graph validation inherits a one-input requirement. Use a timer input in
public pipelines until those two behaviors are unified.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Devices.Wulpus). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
