# Performance

A pipeline does two kinds of work: processing every required sample and displaying
results often enough to read them. Making the screen update faster does not increase
the sensor's sampling rate.

## Use the shared display clock

MOSAIC already has one global `VisualizationTimer`. Charts subscribe at nominally
30 or 60 frames per second. A separate shared `BlockStatusTimer` checks publication-based
status for all blocks every 250 ms. `BaseBlock` manages registration and cleanup.
Do not add a visualization or status timer for each new
block or dispatch a chart update for every input sample.
See [Visualization Overview](visualisation-architecture.md) for the integration pattern.

Bind `VisualizationPanel.Source` to the block's visualization bundle. Each chart monitor
is created when first requested; unused chart types need no chart objects. Hidden feeds
still track timing metadata but skip display copies. Hiding a chart does not stop data
processing or recording.

## Choose batches according to the experiment

A packet can contain several consecutive rows, with channels in columns. This reduces
the number of callbacks needed to deliver the same samples. It is different from an
overlapping window: a packet need not repeat any samples.

At 1,000 samples per second, packets of eight rows arrive about 125 times per second.
Collecting eight live samples can make the first sample wait about 7 ms for the other
seven. A batch suitable for recording may therefore be too large for a feedback loop.
Choose a size that fits the experiment's latency budget, and check that every downstream
block accepts matrix packets. See [Data Shapes and Time](data-and-time.md).

MOSAIC's dispatcher drains up to 64 **already queued** publications in an inner loop.
It never waits for 64 values to arrive and does not combine their payloads. One received
value can be delivered immediately. There is no automatic change from vectors to matrices.

## Keep published values stable

Sliding Window uses a circular buffer: it overwrites old internal rows instead of moving
the remaining rows on each advance. Every published window is still a separate matrix.
Downstream blocks may be reading an earlier window while the next one is being built.

Apply the same ownership rule in new blocks. Reuse private scratch storage where useful,
but do not reuse published arrays until all consumers have finished with them. Avoid
changing rate metadata or notifying properties on each sample when the value is unchanged.

## Watch recording pressure

The recording queue stores numeric snapshots. Its worker formats them and writes batches,
leaving the publishing thread free of numeric-to-text conversion and disk writes.
Faster enqueueing can produce a larger temporary backlog during a burst. Queue capacity
is a limit, not a guarantee that the disk can sustain any data rate.

Check the recording loss/error indicators and the completed file. The default capacity
is 8,192 rows, and a matrix consumes one queue entry per row. Record raw data before
overlapping windows when repeated history is unnecessary. See [Recording](recording.md).

## Measure a change

From the repository root, run the hardware-free harness in Release mode:

```text
dotnet run --project MOSAIC/MOSAIC.Benchmarks -c Release -- output/performance.json
```

It measures three Sliding Window packet sizes, an eight-channel recording, and dispatcher
delivery latency. It checks recording and dispatch for rejected data. Elapsed time,
allocated bytes and process CPU time are medians of seven runs after at least one second
of warmup per case, allowing .NET's tiered compilation to settle; the `details`
object describes the final run, including backlog and the 95th-percentile delivery delay.
CPU times for these short cases are coarse because of the operating system's timer resolution.

Use the same machine, build mode, workload and background load for comparisons. Run once
before changing the code and again afterward, saving separate files. Allocated bytes measure
temporary allocation volume, not peak or retained memory. The harness turns plots off and
uses no hardware: it cannot establish visible frame rate or sensor-to-actuator latency.

For a real experiment, repeat the same graph with charts closed, then visible, then with
recording enabled. Track CPU, allocations, queue growth, rejected values and end-to-end
latency over a sustained run. Keep the original signal rate and verify the resulting data
before deciding whether an optimization helped.
