# Muovi

[Block Catalogue](../block-catalogue.md) / Devices

Streams HD-sEMG / EEG (optionally plus IMU quaternions) from up to 16 OT Bioelettronica Muovi probes attached to a SyncStation, over an outbound TCP connection to `192.168.76.1:54320`.

## Input and output

| Property | Value |
|---|---|
| **Type** | `muovi` |
| **Inputs** | 0; see usage below |
| **Consumes** | vector mode: a tick from an upstream `ClockBlock` (payload ignored). Matrix mode: nothing — the `Muovi-Reader` thread pulls int16 LE bursts (38 raw columns per probe + 6 accessory columns, 18 samples per burst) |
| **Publishes** | `Matrix<double>` of `[samplesPerBatch × OutputChannels]` (matrix mode) **or** `Vector<double>` of `[OutputChannels]` per tick (vector mode). `OutputChannels = probes × (32 + (imu ? 4 : 0))` |

Electrode scaling is mode-dependent: default `emg` uses `1/32768` (normalised full-scale, dimensionless), `emg_lowgain` 0.000572 mV/bit, `eeg` 0.286 µV/bit. IMU columns are raw int16, unscaled.

## Parameters

not positional; an unordered bag scanned by value, read via `ToString()`.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| any | probe index | int | probe 0 | Zero-based probe slot, appended to `ProbeIndices`; if no integer appears, probe 0 is added |
| any | signal mode | string | `emg` | `emg`, `emg_lowgain`/`lowgain`, `eeg` |
| any | `imu` | string | off | Adds 4 quaternion channels per probe |
| any | output mode | string | auto | `matrix`/`matrixmode`/`batch` forces Matrix; `vector`/`vectormode`/`single` forces Vector |

Unrecognised strings are silently ignored. The real knobs are top-level `DesiredRate` (default 100) and `Path`.

## Requirements and use

Connect through the SyncStation and configure the probes/channels. Confirm whether downstream blocks expect vectors or batched matrices; acquisition rate and publish rate need not be equal.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Devices.Muovi). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
