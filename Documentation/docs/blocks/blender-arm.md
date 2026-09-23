# Blender Arm

[Block Catalogue](../block-catalogue.md) / Streaming

Converts a control-algorithm actuation dictionary into a 9-element normalised command vector and ships it as an ASCII JSON array over UDP to the Blender-side Python script.

## Input and output

| Property | Value |
|---|---|
| **Type** | `blenderarm` |
| **Inputs** | exactly 1 (normally a ControlAlgorithm) |
| **Consumes** | `Dictionary<DegreesOfActuation, double>`, values in [0,100]; other types silently ignored |
| **Publishes** | `Vector<double>`, length 9 — [th_flex, th_rot, index, middle, ring, little, wr_flex_ext, wr_uln_rad, wr_sup_pron]; fingers /100 → [0,1], wrist /50−1 → [−1,1], pron/sup negated |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | HostIP | string | `127.0.0.1` | Target IP or hostname (resolved via `UdpClient.Connect`, so names work) |
| 1 | Port | int | 3334 | UDP destination port |

## Requirements and use

Start the matching Blender-side receiver and use its expected channel order and normalization. This output protocol is different from a raw float-vector UDP stream.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Streaming.BlenderArm). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
