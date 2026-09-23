# Troubleshooting

Start with the smallest pipeline that reproduces the problem. The
[first example](first-pipeline.md) separates workbench issues from hardware setup.

## No signal or a flat plot

1. Check that the source is running. Press Play on a Clock; connect and start a device.
2. Check connections and the load report. An unavailable block may leave the rest visible
   but disconnected.
3. Enable the plot and select a channel. A hidden plot does not stop processing.
4. Check signal frequency and sampling rate. A 0.2 Hz sine wave takes five seconds per cycle.
5. Match the source's output shape to the consumer's accepted input.

Use **Show log** in the workbench for application messages. Subscriber failures are
reported in the block's error state and written to the log; developers can attach a
debugger when the logged exception does not explain a processing failure.

## A block is missing or cannot load

Check `Type` against the [Block Catalogue](block-catalogue.md). The block may be excluded
by the platform or build options. Preserve the original file before saving a partially
loaded graph, then inspect its reported block name and error.

For hardware, check vendor libraries, ports, Bluetooth permissions and the connection sequence.
Running MOSAIC on a platform does not imply every vendor's hardware is available there.

## Recording is empty or missing

Check the folder, Record switch and source. Recording captures published output, not incoming
data. Explicit recording paths override the shared destination. Allow the writer to flush
or switch recording off to close the file. See [Recording](recording.md).

## Unexpected data or plots

Before diagnosing an installation, use these checks for a running pipeline.

### Wrong time scale or repeated-looking traces

Check the card's actual feed and the meaning of each row. Sliding Window's own scope
shows its input; a feature scope shows one result per window. Compare with
[Signal Processing Walkthrough](signal-lab.md): 256 source samples/s, four MAV values/s. Display throttling
and large-batch reduction can omit rows; batched-source window metadata has a known
limitation. [Reading the plots](reading-plots.md#display-limits-that-matter) explains both.

### No spectrum or a peak at the wrong frequency

Use a power-of-two window row count. Check the true sample rate and FFT scaling.
With the example's 256-row window at 256 samples/s, expect 1 Hz bins and a peak at 8 Hz.
Do not substitute the four-spectra/s publication rate for the acquisition sample rate.

### Features start low or predictions stay constant

Allow startup padding and the filter to settle. After an amplitude change, wait until
the window contains the new signal. Check the feature itself before the model.
In [the learning example](learning-lab.md), amplitude 1 gives MAV near 0.63 and amplitude
3 gives near 1.90. Capture both classes and keep evaluation capture stopped.

### CSV columns or timestamps look wrong

For matrices, column two is a row index; all rows in one matrix share a publication
timestamp. They are not simultaneous acquisitions. Use the
[CSV reader](recording.md#inspect-a-file-with-python) with the expected channel count
and rows per matrix. Record upstream of windows if you need samples without repeated history.

### Plot updates lag behind processing

Hide unnecessary plots and compare a short recording with the expected output shape/count.
A responsive source does not prove that a downstream processing queue or disk writer is
keeping up. Plot refresh, processing delivery and CSV queues have different policies;
reducing display work does not remove a slow operation in the processing chain.

## Build or launch errors

| Symptom | What to check |
|---|---|
| No compatible SDK | Run `dotnet --version` from the solution directory; inspect `global.json`. |
| Multiple target frameworks | Include the appropriate `-f` argument from [Getting Started](getting-started.md). |
| Missing Delsys package | Delsys is disabled by default. Configure an authorised private vendor package source before using `-p:EnableDelsys=true`. |
| Missing platform workload | Install the workload and platform SDK for the selected application project. |
| iOS signing error | Select your own team, identity and device on the build Mac. |
| Bluetooth unavailable | Use the platform-specific target and check permissions and backend. |

## Documentation build errors

See [Building the Documentation](building-documentation.md) for the pinned DocFX tool
and the fix for locked `_site/toc.json`. Generated output should not be tracked in Git.
