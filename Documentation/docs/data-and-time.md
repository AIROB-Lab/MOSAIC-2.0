# Data shapes and time

A connection carries a value. To interpret that value you need to know **what its elements
represent**, not just whether it is a vector or a matrix. Use the
[Signal Processing Walkthrough](signal-lab.md) to inspect these cases in a running pipeline.

## Samples, batches, windows and features

| Payload | Meaning of one row or element | Example |
|---|---|---|
| Channel vector | Simultaneous values from several channels at one sample instant | Four channels: [0.1, −0.2, 0.4, 0.0]. |
| Sample batch | Consecutive sample instants in rows; channels in columns | 4 × 2 contains four instants from two channels. |
| Sliding window | A recent history of samples; may reuse rows from the preceding window | 256 × 1, advanced by 64 rows. |
| Feature vector | One derived quantity per channel or feature | MAV of a 256-row window becomes one value per channel. |
| Spectrum matrix | Rows are frequency bins, columns are channels | FFT: 129 × 1; row 8 means 8 Hz in the signal processing example. |
| Image matrix | Rows and columns are spatial/depth positions, as defined by the device | An ultrasound frame is not a sequence of time samples. |

A matrix-to-vector conversion does not automatically preserve meaning. Flattening an image
produces pixels; reducing a time window produces features; replaying its rows produces a
sample stream. Check a block's **Consumes** and **Publishes** fields before connecting it.

## Follow the rates in the signal processing example

| Quantity | Value | What it describes |
|---|---|---|
| Sine frequency | 8 Hz | Eight cycles of the waveform per signal second. |
| Source sample rate | 256 samples/s | Sample spacing of 1/256 s, approximately 3.906 ms. |
| Source publication rate | 256 outputs/s | One channel vector per Clock tick. |
| Window span | 256/256 = 1 s | History used by one result. |
| Window stride | 64 samples | New rows between consecutive outputs. |
| Window publication rate | 256/64 = 4 outputs/s | One window every 250 ms in steady operation. |
| MAV feature update rate | 4 values/s per channel | One feature result per window. |
| FFT spectrum update rate | 4 spectra/s | Each spectrum still describes the original 256 samples/s signal. |
| Screen refresh | Separate display schedule | How often buffered results are redrawn, not how often data is acquired. |

Windowing groups existing samples. Resampling changes the output sample stream.
Feature extraction changes what a value means. None of these operations is equivalent
to changing how often the screen redraws.

## Batches are not necessarily overlapping windows

[Download batched-signal.json](../examples/batched-signal.json). This alternative uses a
64 ticks/s Clock and four scans per Sin Generator packet:

**64 packets/s × 4 new rows/packet = 256 samples/s.**

Successive packets contain new samples, with no overlap. The generator carries its phase
across packet boundaries. Compare it with the example's source: both describe an 8 Hz
sine sampled at 256 samples/s, but one publishes matrices and the other vectors.

Sliding Window counts input rows. With stride 64, both forms produce **4 windows/s**:
256 samples/s divided by 64 new rows per window. Its publication metadata now reports
4, while the samples inside each window retain `SignalRate = 256`. The documentation
tests check this batched case. Metadata describes the nominal cadence; it does not
measure whether a device or processing branch is keeping up.

## What the rate fields mean

For users, begin with the payload and units above. For developers:

- `SignalRate` carries the sample rate used by sample-domain processing such as FFT.
- `DesiredRate` supplies an expected publication rate, or paces a source that uses it.
- `InputRate` inherits the upstream desired rate; it is not a measurement of rows/s.
- A visualization has its own explicit/derived rate configuration. A block's inherited
  sample-rate field alone does not tell you how its feature plot is timed.

Sample-rate changes propagate through ordinary processing blocks. Blocks that change
the rate, such as Resampler, retain their own output rate. Publication-rate changes use
the model's propagation helpers; source pacing remains block-specific. A saved `DesiredRate` does not universally
resample data or synchronize an independently clocked device.
See [runtime propagation](core-architecture.md#sample-rate-and-output-rate) and
[implementing rate changes](advanced-blocks.md#give-the-visualization-the-right-rate).

## Multiple sources and missing data

Joiner combines the latest values and publishes when its designated timer input arrives.
It does not synchronize acquisition timestamps. A slow input can contribute the same held
value to several outputs.

[Resampler](blocks/resampler.md) uses the nominal input sample spacing, so splitting
identical samples into different packet sizes does not change its results. It uses
zero-order hold: a new output between two input instants repeats the preceding value.
Those intermediate outputs arrive when the next input arrives, rather than from an
independent timer. It does not synchronize device clocks or remove aliasing; low-pass
filter a signal appropriately before reducing its sample rate. Supply contiguous rows,
not overlapping windows, because every input row is treated as a new sample.

Processing delivery, display buffers and CSV writing have different queue policies.
A responsive plot does not establish that a recording is complete. For timing-sensitive
experiments, check the device timestamps and recorded counts separately.
