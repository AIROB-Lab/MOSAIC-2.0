# Stimulus

[Block Catalogue](../block-catalogue.md) / Devices

Automated cue generator: walks a list of target activation vectors through a timed FSM (rest → rise → hold → capture → fall → next task) and publishes the current state plus interpolated target each tick, so downstream trainers know when to record.

## Input and output

| Property | Value |
|---|---|
| **Type** | `stimulus` |
| **Inputs** | exactly 1 — a tick source; the sender is never inspected |
| **Consumes** | one tick per `OnReceive`, payload ignored. All timing comes from an internal `Stopwatch`; the tick only samples the FSM |
| **Publishes** | `ValueTuple<string, Vector<double>>` = `(State, TargetValue)`, State one of `rest`, `rise`, `hold`, `capture`, `fall`. The vector is the current task's target scaled by sin² during rise and cos² during fall |

## Parameters

positional and variadic; **at least 2 required**, fewer throws `ArgumentException`.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | captureDuration | double | 5.0 | Capture window, seconds |
| 1..N | task | string | — | One task per param: `"name:v1;v2;v3"` or bare `"v1;v2;v3"` (auto-named `Task[i]`) |

Task values are parsed with `double.Parse(InvariantCulture)` — a malformed number **throws** `FormatException` rather than defaulting. The other durations (rest 2.0, rise 0.5, hold 1.0, fall 1.0) are hard-coded and not configurable.

## Requirements and use

Configure the task sequence, targets and timing before starting it. Connect compatible trigger/training blocks and verify that cue labels and target dimensions match the experiment.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.Devices.Stimulus). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
