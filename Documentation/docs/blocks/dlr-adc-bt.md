# DLR ADC/BT

[Block Catalogue](../block-catalogue.md) / Devices

Acquires analogue samples over a Bluetooth serial port.

## Input and output

| Property | Value |
|---|---|
| **Type** | `dlradcbt`, `dlr_adcbt`, `dlradc` |
| **Inputs** | 1; see usage below |
| **Consumes** | clock ticks; device frames arrive over serial |
| **Publishes** | `Vector<double>` in volts, one element per channel |

## Parameters

| Index | Parameter | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | Port | string | empty | Serial port, e.g. COM7 |
| 1 | Channels | int | 10 | Number of ADC channels |
| 2 | BaudRate | int | 115200 | Serial speed |

## Requirements and use

Requires a paired serial device and a working serial backend. Connect the port and start the Clock. When no complete device frame is available, the previous sample is held; a steady publish rate alone does not prove fresh acquisition.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Devices.DlrAdcBt). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
