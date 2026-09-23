# BodyRig

[Block Catalogue](../block-catalogue.md) / Devices

DLR BodyRig inertial motion capture. Takes IMU quaternions from an RN41 Bluetooth serial port (clock-driven) or from an upstream block, runs forward kinematics over a calibrated segment chain, and publishes per-segment orientation and position.

## Input and output

| Property | Value |
|---|---|
| **Type** | `bodyrig` |
| **Inputs** | exactly 1 — the sender's *type* picks the mode: a `ClockBlock` → Serial, anything else → Upstream |
| **Consumes** | Upstream: `Vector<double>`, 4 doubles per sensor as `[w, x, y, z]`. Serial: nothing from the pipeline (the tick is only a pump; 22-byte frames arrive at 115200 8N1) |
| **Publishes** | `Vector<double>` of length `numChannels × 7`, per segment `[W, X, Y, Z, posX, posY, posZ]` |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | portName | string | `""` | Serial port, e.g. `COM5` |
| 1 | numChannels | int | 10 | Chain segments / IMU slots, clamped ≥1 |
| 2 | calibrationDirectory | string | `null` | Directory scanned for profiles; empty/whitespace → `null`, skipping the scan |
| 3 | profileFileName | string | `""` | Bare file name, matched case-insensitively inside [2] |

Read via `ToString()`, not the `JsonModel.GetX` helpers — a non-numeric channel count silently leaves 10, and an unmatched profile name silently leaves the first file in the directory selected.

## Requirements and use

Connect the serial device or supply quaternion vectors, then select a calibration profile matching the sensor layout. Confirm sensor order and segment geometry before interpreting positions. The Unity side channel uses localhost port 3339.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Devices.BodyRig). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
