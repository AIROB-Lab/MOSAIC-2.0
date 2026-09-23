# Delsys

[Block Catalogue](../block-catalogue.md) / Devices

Delsys Trigno RF wireless EMG source. Discovers, arms and streams from Trigno sensors, flattening every channel (EMG, accelerometer, gyroscope, quaternion) of every selected sensor into one vector per sample.

## Input and output

| Property | Value |
|---|---|
| **Type** | `delsys` |
| **Inputs** | 0; see usage below |
| **Consumes** | nothing |
| **Publishes** | `Vector<double>`, one element per channel |

Column order: by `SidOrder` position, then SID numerically, then a canonical `ChannelSortKey` (type → axis → number) within a sensor — *not* raw channel-name alphabetical, despite the XML comment on `SidOrder`. Roles are exposed as `EmgIndices`, `AccIndices`, `GyroIndices`, `OrientationIndices`, `ImuIndices`.

## Parameters

position-independent. Every entry is scanned: anything that `int.TryParse`s sets `DefaultModeIndex` (last wins); otherwise an entry containing `async` sets `AsyncMode`; otherwise one containing `sync` sets `SyncMode`.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| any | DefaultModeIndex | int | `null` | Parsed and logged, then never used |
| any | AsyncMode | string `async` | false | Flag only; the dispatch timer starts regardless |
| any | SyncMode | string `sync` | false | Enables the `OnReceive` path, which is unreachable |

## Requirements and use

The public build disables this integration by default and does not redistribute
the Delsys SDK packages or institutional credentials. Authorised users must
configure their own private vendor package source, set the environment variables
`MOSAIC_DELSYS_API_KEY` and `MOSAIC_DELSYS_API_LICENSE`, and build with
`-p:EnableDelsys=true`. Never commit those values.

Runtime use also requires the Delsys vendor software and connected Trigno
hardware. Scan/pair sensors and choose channels on the card. In synchronous
mode, connect the clock and check its rate against the acquired signal rate.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

The default public documentation build cannot generate an API page for this block
because the vendor SDK is not redistributed. Build MOSAIC with `EnableDelsys=true`
and your authorised packages to compile and inspect the integration source.
