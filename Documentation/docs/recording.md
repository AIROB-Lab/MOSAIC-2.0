# Recording and Saved Pipelines

Recording saves published values as CSV. Saving a pipeline saves the arrangement and
configuration of blocks. These are separate operations.

## Record from the workbench

1. Use **Set recording folder…** in the desktop toolbar to choose a writable folder.
2. Open each block's settings menu and tick **Record to CSV** for the outputs you want.
   The add-block dialog also offers recording when a destination is configured.
3. Start the source. Only values the block publishes are recorded.
4. Stop the source and switch recording off when finished. Allow the background writer
   to finish before opening or copying the files.

The shared destination is remembered on this machine, not embedded in the pipeline
you send to a colleague. Mobile file-picking controls and their placement differ.

## Find the files

Recordings started using the shared folder have this layout:

```text
chosen-folder/
  20260911_143000/
    Signal_sin.csv
    Filtered_filtered.csv
```

The timestamped folder is created when recording first needs it. Blocks share that session.
Files are named `<block name>.csv`; many blocks append a type suffix, for example
`Signal_sin.csv`, `Window_windowed.csv`, `MAV_mav.csv` and `Spectrum_fft.csv`. Restarting the app creates a new session on
the next recording. Changing the root folder resets the shared destination for new
recordings. Blocks already recording keep their open file; switch them off and on to
move them to the new destination.

## What saving preserves

| Setting or data | Saved in pipeline JSON? |
|---|---|
| Block types, names, inputs and exported parameters | Yes. |
| Explicit per-block `Path` | Yes, where supported by the block. |
| Machine's shared recording folder | No; stored in local application settings. |
| Record switch enabled using the shared folder | No; enable it again after loading. |
| Current samples and plot history | No. |
| Device connection or trained model | Block-specific; use the device/model's setup or persistence controls. |

Save a copy before sharing and check it by reopening. Remove machine-specific paths or
explain the folders and dependencies the recipient needs.

## Explicit paths in JSON

For most blocks, `Path` is a **directory** enabling recording immediately on load:

```json
{
  "signal": {
    "Type": "SinGenerator",
    "Inputs": ["Clock"],
    "Params": [1, 1.0, 1.0, 0.0, 0.0, 1],
    "Path": "C:\\Data\\MOSAIC"
  }
}
```

This fragment requires a `Clock` block. Replace the path with a writable directory on
the device; a Windows drive path does not work on macOS or iOS.

An explicit recording path takes precedence over the shared folder and is reused after
toggling recording. It does not automatically gain the shared timestamped subfolder.
Some predictor blocks use `Path` for model storage instead; check their reference.
Their Record switch can still use the shared destination.

## Read and interpret the CSV

The numeric files have no header. The layout depends on the published payload:

| Payload | Columns | Example line |
|---|---|---|
| Scalar | timestamp, value | `123.5,0.25` |
| Two-channel vector | timestamp, channel 0, channel 1 | `123.5,0.25,-0.5` |
| Labelled vector | timestamp, label, channel values | `123.5,Low,0.63` |
| Two-column matrix | timestamp, zero-based row index, column values | `123.5,0,1,2` |

The timestamp is publication time in seconds, using the runtime's monotonic clock anchored
to wall-clock time. It is not automatically the hardware acquisition time of each sample.
All rows from one matrix share a timestamp. For example, a two-row matrix is:

```text
123.5,0,1,2
123.5,1,3,4
```

Here the matrix is `[[1,2],[3,4]]`; the second CSV column is an index, not a signal channel.
A later matrix starts at row index 0 again. Check both row indices and expected dimensions;
an incomplete file can otherwise look like a smaller valid matrix.

In [Signal Processing Walkthrough](signal-lab.md), Signal and MAV use vector layout, Window uses 256-row
matrix layout, and Spectrum uses 129-row matrix layout. Spectrum's row index means a
frequency bin: index 8 is 8 Hz in this configuration. It does not mean a time sample.

The generic dumper ignores unsupported payload types. Trigger Buffer's published segment
count does not serialize its stored training segments; use that block's export workflow.

### Choose where to record

| Record this block | To retain | Interpretation |
|---|---|---|
| Signal/source | Raw published samples or packets | Keep device units, rate and channel map with the experiment. |
| Filtered | Processed sample stream | Filtering has already changed the waveform. |
| Window | Every complete published matrix | Overlapping windows repeat rows; startup includes padding. |
| MAV | One feature per channel and window | Four feature rows/s nominally in the signal processing example. |
| Spectrum | Frequency-bin matrices | Store FFT settings and original sample rate with the CSV. |
| Classifier | Class-score vectors | Keep the class order; a score vector is not a label string. |

Any scope-side trimming or display reduction does not alter what the block publishes to
recording. A Window recording therefore retains repeated history even when a downstream
scope displays only new rows. For a continuous raw trace, record the source or pre-window
sample stream. Do not estimate its sampling rate from successive matrix-row timestamps.

### Inspect a file with Python

Download [read_recording.py](../examples/read_recording.py) and the small
[matrix-layout.csv example](../examples/matrix-layout.csv) into the same folder.
The reader uses only the Python standard library:

```text
python read_recording.py matrix-layout.csv --layout matrix --channels 2 --rows-per-matrix 2
```

Expect four CSV rows, two matrices and a publication timestamp span of 0.25 seconds.
For recordings from this example, use vector layout with one channel for Signal/MAV,
or matrix layout with one channel and 256 rows per matrix for Window:

```text
python read_recording.py Signal_sin.csv --layout vector --channels 1
python read_recording.py Window_windowed.csv --layout matrix --channels 1 --rows-per-matrix 256
```

Replace filenames with the actual files; most blocks append a type suffix to the block name. The reader
checks row layout and matrix boundaries. It does not recover missing samples, undo
overlap, reconstruct hardware timestamps or prove lossless acquisition.

### Check completeness before relying on a recording

Enqueueing copies the numeric values; CSV formatting and batched file writes happen on
the background writer. Reusing an input buffer after enqueueing cannot change its saved
values. The CSV layout above remains the same for single values and batches.

Writing is asynchronous and normally flushes every ten seconds and on close, so the
file may lag behind the plot. Each writer has a bounded queue. If storage cannot keep
up, new rows are rejected and counted; accepted rows are kept in order. The block card
reports recording row loss and writer errors separately from its activity indicator.
A lost-row count greater than zero means the file is incomplete, even if the plot looks normal.

Flush requests wait for queue space and cannot be discarded by later data. A completed
flush covers accepted rows ahead of that request; it cannot recover rejected rows.
Closing the writer drains accepted rows. Disk failures remain visible as recording errors.
Check duration, channel count, and sample count in a short trial before recording an experiment.

If no file appears, check the destination, Record switch, source, and published output,
in that order. See [Troubleshooting](troubleshooting.md).
