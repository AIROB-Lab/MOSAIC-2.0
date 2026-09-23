# SiFi

[Block Catalogue](../block-catalogue.md) / Devices

Source for SiFi Labs BLE biosignal wearables (BioArmband / BioPoint), driven by an external `sifibridge` child process whose JSON stdout is parsed into EMG/IMU/ECG/EDA/PPG streams.

## Input and output

| Property | Value |
|---|---|
| **Type** | `sifi` |
| **Inputs** | 0; see usage below |
| **Consumes** | nothing from the graph; newline-delimited JSON from the `sifibridge` subprocess's stdout |
| **Publishes** | heterogeneous, three runtime types on one port: `Matrix<double>` `[samples × channels]` for armband EMG, `Vector<double>` for single-channel EMG, and `ValueTuple<string, Vector<double>>` tagged `"imu"`, `"ppg"`, `"ecg"`, `"eda"` |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | macAddress | string | required | Address of the user's paired device |
| 1 | bridgePath | string | `"sifibridge"` | Executable, resolved on PATH |
| 2 | mainsNotch | enum | `on50` | `off` / `on50` / `on60`; unparsable falls back to On50 |
| 3 | bandpassLow | double | 20 | Hz |
| 4 | bandpassHigh | double | 450 | Hz |

## Requirements and use

Requires the SiFi bridge and a paired device. Confirm the requested signal channels and connection state on the card before starting acquisition.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Devices.SiFi). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
