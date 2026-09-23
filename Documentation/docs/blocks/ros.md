# ROS

[Block Catalogue](../block-catalogue.md) / Streaming

A rosbridge_suite bridge over WebSocket (`ws://IP:9090`): subscribes to a topic and publishes each message into the pipeline, or advertises a topic and forwards pipeline vectors/matrices out as `std_msgs` data arrays.

## Input and output

| Property | Value |
|---|---|
| **Type** | `ros`, `rosblock` |
| **Inputs** | 0; see usage below |
| **Consumes** | publish mode only: `Vector<double>` or `Matrix<double>` (row-major); other types produce an empty payload and are dropped |
| **Publishes** | subscribe mode only: flat `Vector<double>`, length = incoming array length. `Channels > 1` only permutes elements into channel-major order — never a matrix |

## Parameters

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | IP | string | `localhost` | rosbridge host |
| 1 | Action | string | `subscribe` | `publish` or `subscribe`, compared case-insensitively |
| 2 | Topic | string | `ros_topic` | Topic name; the block prefixes `/` itself |
| 3 | MessageType | string | `std_msgs/Float64MultiArray` | ROS message type |
| 4 | Channels | int | 1 | Subscribe mode only; reorders the flat output |

## Requirements and use

Use the exact action string and a running rosbridge endpoint with the expected topic/message schema. Connection can begin during construction; inspect connection status before publishing.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Streaming.ROS). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
