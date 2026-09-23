# Cross Correlation

[Block Catalogue](../block-catalogue.md) / Signal Processing

Compares two channels to estimate their relative lag.

## Input and output

| Property | Value |
|---|---|
| **Type** | `crosscorrelation` |
| **Inputs** | one observation stream |
| **Consumes** | sample matrix (rows = time, columns = channels) or interleaved vector; numeric mode uses the first two matrix columns or two interleaved channels, named mode uses its configured channel count |
| **Publishes** | legacy: correlation vector; extended: `[peak lag, sample rate / lag, reserved zero]` |

## Parameters

There are two supported formats. Choose by the output you need; changing the format
also changes what downstream blocks receive.

| First parameter | Output | Use |
|---|---|---|
| A number, such as `36` | The complete correlation curve | Inspect the curve, or reopen an existing numeric-format pipeline. This is still the palette default. |
| A string, such as `"Lags:1:36"` | `[peakLag, sampleRate / peakLag, 0]` | Use the detected lag as a downstream feature. |

### Named lag format

Example parameter list: `["Lags:1:36", 1000, 1, 4, "LowPass:3", "HighPass:70", "MovMedian:5"]`.

| Index | Parameter | Default if omitted | Meaning |
|---|---|---|---|
| 0 | Lag range | Required for this mode | `Lags:min:max` selects an inclusive range; `Lags:36` means -36 through 36. |
| 1 | BufferLen | 1000 | Retained samples per channel. |
| 2 | ProcessEveryN | 5 | Calculate once per this many accepted packets. |
| 3 | ChannelCount | 2 | Number of interleaved channels; at least 2. |
| 4 onward | Optional tokens | Filter windows 1; channels 0 and 1 | `LowPass:n`, `HighPass:n`, `MovMedian:n`, `Channels:a:b`. Filter sizes are samples, not Hz. |

The example chooses four channels and calculates on every packet. A filter window of
1 disables that stage. Changing the selected channels clears the accumulated samples.
The palette exposes the first three fields; use JSON for channel count and filter tokens.

### Numeric format (backward compatible)

| Index | Parameter | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | MaxLag | int | 0 | Legacy mode: zero selects full range |
| 1 | BufferLen | int | 1000 | Legacy minimum is 511 samples |
| 2 | ProcessEveryN | int | 5 | Process once per this many packets |

## Requirements and use

The legacy output is scaled to L2 norm 10, not a Pearson coefficient. Wait for enough nonconstant samples. Extended mode starts with `Lags:max` or `Lags:min:max`, followed by buffer length, process interval, channel count, then optional `LowPass:n`, `HighPass:n`, `MovMedian:n`, and `Channels:a:b` tokens. Channel indices are zero-based; −1 selects the average. Extended and legacy outputs have different meanings.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Implementation

[API reference](xref:MOSAIC.Models.SignalProcessing.CrossCorrelation). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
