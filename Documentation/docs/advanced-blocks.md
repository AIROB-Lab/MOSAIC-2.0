# Batches, features and source blocks

Start with [Build Your Own Block](new-block-guide.md) for factory registration, palette
metadata and cards. This page extends that contract to sample matrices, derived features,
source lifetimes and several inputs.

This is the second developer tutorial. First complete the Gain example so you have
seen a block receive a vector, publish a result and appear in the palette.
A **batch** contains several sample instants at once; a **feature** summarizes those
samples. RMS (root mean square) is one such summary: square the values, average them,
then take the square root.

## Implement a window-to-feature block

[Download WindowRms.cs](../examples/WindowRms.cs). This complete teaching class accepts a
sample matrix and publishes one RMS value per channel. It is compiled and exercised by
the documentation tests; it is not registered as a shipped block.

For input `[[3,0],[4,2]]`, expect `[sqrt(12.5),sqrt(2)]`. Rows are time samples and
columns are channels. It rejects empty, unsupported or non-finite input without publishing,
and never modifies the caller's matrix.

Invalid input returns without publishing. Like the Gain example, this class leaves
status updates to `BaseBlock`; it does not assign a status for each received value.

The first channel contains 3 and 4: its RMS is `sqrt((3² + 4²) / 2)`, about 3.536.
The second contains 0 and 2: its RMS is about 1.414. The result is one two-channel
vector, even though the input contained two sample instants.

Copy it into your shared application project and keep or change its namespace. Register
construction in `BlockFactory.ResolveRegistration`:

```csharp
"windowrms" => (typeof(MOSAIC.Documentation.Examples.WindowRms),
    () => MOSAIC.Documentation.Examples.WindowRms.ConfigureInput(sp, m)),
```

Add a palette descriptor, using the fully qualified class if needed:

```csharp
new("windowrms", typeof(MOSAIC.Documentation.Examples.WindowRms),
    "Window RMS", BlockCategory.SignalProcessing,
    "Computes one RMS feature per channel from a sample window.", _none),
```

Replace MAV in a copy of the signal processing example with this definition:

```json
"RMS": { "Type": "windowrms", "Inputs": ["Window"] }
```

This is a fragment; keep Clock, Signal, Filtered and Window in the complete graph.
The amplitude-1 sine should give RMS near **0.707**, at four feature updates/s.
Add a card with [Visualization Panel Integration](visualization-panel.md) and bind its
Source to `Block.Viz`, or record the output without a custom card.

The example explicitly sets its feature output rate from the incoming publication rate.
It does not use the input matrix's 256 samples/s as the feature time base. Consequently,
downstream blocks must treat its output as a feature sequence, not raw waveform samples.
Its rate still depends on correct upstream metadata; it cannot repair a source's wrong rate.

The class inherits the default one-input constraints and empty parameter export.
It also needs no `Viz.UpdateSignalRate` call for its one-vector-per-window plot:
the `Visualization` override lets `BaseBlock` supply publications/s, and `Feed(result)`
identifies one plotted time point per publication. The explicit **model** rate updates
remain necessary because the feature stream has a different sample meaning from its
input window. Removing those would leave downstream DSP using the acquisition rate.

## Give the visualization the right rate

Publication metadata and visualization rate declarations serve different purposes.

| Your block emits | Scope setup | Reason |
|---|---|---|
| One raw sample vector per publication | Expose `Visualization`; the bundle derives its rate from publications/s. | Each point is one sample instant. |
| Fixed-size contiguous matrix packets | Expose `Visualization`; the bundle derives publications/s × rows per feed. Leave overlap trimming unset. | All packet rows are new data. |
| One feature vector per window | Expose `Visualization` and give the model the correct feature output rate. | One point represents one complete window result. |
| Overlapping sample windows | Explicitly configure sample rate and desired rate only when tail selection is appropriate. | Their ratio estimates new rows per publication. |

For example, a model publishing 64 packets/s with four new rows per packet needs
only `Publish(packet)` and `Viz.Feed(packet)` after exposing `Visualization`;
the bundle derives 256 samples/s. This relies on correct publication metadata and
does not detect overlap or infer the physical meaning of matrix rows.

If publication rate and packet size cannot express the time base (for example,
variable packet sizes at a known 256 samples/s), declare the row sample rate:

```csharp
Viz.UpdateSignalRate(256);
Publish(packet);
Viz.Feed(packet);
```

For a visualization that deliberately displays only the newest 64 rows of each
256-row window:

```csharp
Viz.UpdateSignalRate(256);
Viz.UpdateDesiredRate(4);
Viz.Feed(window);
```

These are integration excerpts. Setting DesiredRate on the bundle opts into a matrix-tail
rule; automatic BaseBlock publication-rate updates use a separate method. Do not enable
trimming for arbitrary images, features or variable-sized contiguous packets. Scope
display throttling and batch reduction still apply after this selection.

Override the protected `Visualization` property to expose your owned bundle.
Call `RefreshVisualizationRate()` if you assign it later, feed stable results, and dispose
it exactly once through an idempotent owner. See [Reading the plots](reading-plots.md)
for the display's practical limits.

## Process batches without replaying state

If a block accepts both vectors and sample matrices, process matrix rows in order.
Carry a stateful filter across contiguous rows and packets; do not reset it per packet
unless that is the operation you intend. Allocate a stable output for each publication.

An overlapping window is different: a stateful filter must not blindly treat every row
as newly acquired data. Put continuous filtering before window assembly, or implement
an explicit state/window policy and document it. The existing
[Filter](blocks/filter.md) and [Sliding Window](blocks/sliding-window.md) illustrate the
two separate responsibilities.

Test two packet partitions of the same sample sequence. Their output should agree if
packet boundaries have no mathematical meaning for your operation. Also test channel
changes, invalid rows, and setting changes during processing.

## Add a source with an explicit lifetime

A source needs no observation input. A clock-driven source still needs a Clock connection;
a device-owned acquisition loop does not. Use literal input constraints for whichever
contract you choose.

For a new asynchronous device source, implement this lifecycle:

| Operation | Required ownership |
|---|---|
| Connect | Create/open the transport and validate settings; report connection failures. |
| Start | Create one cancellation source and one retained worker task; reject duplicate starts. |
| Read | Pass cancellation into reads, parse complete frames, and publish stable snapshots. |
| Stop | Request cancellation, unblock pending I/O, await worker completion, then release transport. |
| Dispose | Prevent restart and repeat cleanup safely; dispose visualization and base resources. |

Keep hardware I/O out of the UI thread. A cancellation flag alone cannot stop a blocking
driver call; the transport needs cancellation support or an explicit close/unblock path.
Do not leave an unobserved fire-and-forget task running after the block is removed.

`PipelineSession` uses `BaseBlock.DisposeAsync()` for final cleanup. Keep your existing
`Dispose` override, and use `TrackCleanup(task)` when it starts asynchronous transport
shutdown or closes an additional recording. The base awaits those tasks. Shutdown rejects
new input and discards queued input to stopping blocks; it waits for active callbacks and
flushes rows already accepted by their recording writers. It is not a lossless pipeline drain.

The existing ClockBlock and SinGenerator separate tick scheduling from generation.
For a network-owned loop, inspect [UDP Client](blocks/udp-client.md) and its disposal
implementation. Driver-specific connection code is not interchangeable across devices.

## Give multiple inputs distinct roles

Use the sender to route observations, labels and timing events. Input order may define a
layout, as it does for Joiner. Declare that order in the reference and example JSON.

Choose the operation's policy before implementing it: latest-value join, one-to-one
pairing, timestamp alignment, or trigger-controlled capture. Different upstream publishers
can call a consumer concurrently. Protect shared buffers and settings, and never assume
that a label event has arrived just because an observation was published later elsewhere.

Use a short table in your block documentation:

| Input | Payload | On arrival |
|---|---|---|
| Observations | Feature vector with D entries | Buffer or predict according to current state. |
| Labels | Target vector or stop marker | Change capture state; do not append it as an observation. |
| Timer, if used | Tick | Publish a snapshot according to the selected policy. |

[The learning tutorial](learning-lab.md) shows how observation and control paths differ.

## Run the example checks

From the repository's MOSAIC solution directory:

```text
dotnet test MOSAIC.Tests/MOSAIC.Tests.csproj --filter FullyQualifiedName~DocumentationWorkflowTests
```

The tests compile the downloadable Gain and RMS classes, check their transformations,
input ownership, invalid-input behavior, Gain configuration round-trips and RMS feature
rate. They also load the user tutorial graphs, check
MAV/FFT results, train two classes, and verify CSV rows. These checks run without chart
rendering; finish a new card by checking visibility, settings, pop-out and removal in the app.
