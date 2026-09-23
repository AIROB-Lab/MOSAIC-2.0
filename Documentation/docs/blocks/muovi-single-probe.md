# Muovi Single Probe

[Block Catalogue](../block-catalogue.md) / Devices

The same decode as Muovi, but for one probe connecting directly over Wi-Fi with no SyncStation: this block is the TCP *server*, binding `0.0.0.0:54321` and waiting for the probe to dial in.

## Input and output

| Property | Value |
|---|---|
| **Type** | `muovisingleprobe` |
| **Inputs** | 0; see usage below |
| **Consumes** | a tick in vector mode (payload ignored); otherwise nothing — the reader thread pulls 76-byte samples (38 int16 columns) off the accepted socket |
| **Publishes** | `Matrix<double>` `[samplesPerBatch × OutputChannels]` or `Vector<double>` `[OutputChannels]` per tick. `OutputChannels = 32 + (imu ? 4 : 0)` |

Uses the same `MuoviConstants.ConversionFactor` table: electrode units are `1/32768` normalised in the default `emg` mode, mV/bit only in `emg_lowgain`, µV/bit in `eeg`. IMU raw int16.

## Parameters

keyword-only bag, no positions.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| any | signal mode | string | `emg` | `eeg`, `emg_lowgain`/`lowgain`, `emg` |
| any | `imu` | string | off | Adds 4 quaternion channels |
| any | output mode | string | auto | `matrix`/`matrixmode`/`batch` or `vector`/`vectormode`/`single` |

## Requirements and use

This block listens for the probe to connect, rather than dialing a SyncStation. Configure the Wi-Fi probe to reach the host and ensure that its receive port is free.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Devices.MuoviSingleProbe). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
