# Quattrocento

[Block Catalogue](../block-catalogue.md) / Devices

Streams multichannel EMG/EEG/AUX from an OT Bioelettronica Quattrocento amplifier over TCP (default `169.254.1.10:23456`), decoding the device frame down to only the inputs the user enabled.

## Input and output

| Property | Value |
|---|---|
| **Type** | `quattrocento`, `quattrocentoblock` |
| **Inputs** | 0; see usage below |
| **Consumes** | nothing from the graph. Off the socket: int16 LE frames of 120/216/312/408 columns per sample, NCH auto-derived as the smallest preset covering the enabled inputs |
| **Publishes** | `Matrix<double>` of `[samplesPerBatch × (enabledBio + (aux ? 16 : 0))]`; bio columns scaled to mV, AUX to volts. `samplesPerBatch = sampleRate / max(1, DesiredRate)` — at the default `DesiredRate` 16 and 10240 Hz that is a 640-row matrix per publish |

## Parameters

order is irrelevant; each param is a `"key:value"` string.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| any | `ip:` | string | `169.254.1.10` | Amplifier address |
| any | `port:` | int | 23456 | TCP port |
| any | `fsamp:` | int | 3 | Clamped 0..3 → 512 / 2048 / 5120 / 10240 Hz |
| any | `decim:` | int | true | Non-zero → decimation on |
| any | `nch:` | int | — | Accepted and deliberately ignored for backward compatibility |

A param with no `:` is skipped silently; an unparsable numeric value leaves the default.

## Requirements and use

Configure channel count, gains and acquisition settings for the connected amplifier before streaming. Check the sample rate and batch shape before choosing downstream processing.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Devices.Quattrocento). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
