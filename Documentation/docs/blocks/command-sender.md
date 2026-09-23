# Command Sender

[Block Catalogue](../block-catalogue.md) / Devices

Fire-and-forget UDP command sender for ESP-based IMU nodes. Sends ASCII strings like `ROT1on` (`{ROT|ACC|GYR|MAG|ALL}{deviceNo}{on|off}`) to start/stop streams. Control plane only — it carries no pipeline data.

## Input and output

| Property | Value |
|---|---|
| **Type** | `commandsender` |
| **Inputs** | 0; see usage below |
| **Consumes** | nothing; `OnReceive` is empty |
| **Publishes** | nothing; output is exclusively the UDP datagram |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | hostIP | string | `"192.168.1.255"` | Target address; `.255` is a broadcast address by design (`EnableBroadcast` is set unconditionally) |
| 1 | port | int | 10015 | UDP port; a non-numeric value **throws** `ArgumentException` |

Constructor defaults (`192.168.1.1` / `5000`) differ from these `ConfigureInput` defaults.

## Requirements and use

Set the destination host/port to your ESP nodes and send the appropriate start/stop command from the card. This block controls another source; it does not acquire IMU samples itself.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Devices.CommandSender). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
