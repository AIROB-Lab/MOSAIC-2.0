# Filter

[Block Catalogue](../block-catalogue.md) / Signal Processing

The general-purpose time-domain frequency filter: an independent IIR (Butterworth) or FIR instance per channel, state carried across samples, parameters adjustable at runtime.

## Input and output

| Property | Value |
|---|---|
| **Type** | `filterblock`, `mosaic.models.filterblock`, `filter` |
| **Inputs** | exactly 1 |
| **Consumes** | `Vector<double>`, `double[]`, scalar `double`, or `Matrix<double>` [timesteps × channels] |
| **Publishes** | `Vector<double>` for `Vector<double>` **and** for `double[]` (a type change); scalar `double` for scalar; `Matrix<double>` of the same shape for matrix input |

## Parameters

the Depth Filter layout minus its 7th entry. All optional and cumulative.

| # | Name | Type | Default | Meaning |
|---|---|---|---|---|
| 0 | FilterType | string | `Lowpass` | Lowpass \| Highpass \| Bandpass \| Bandstop |
| 1 | Implementation | string | `IIR` | IIR \| FIR |
| 2 | CutoffLow | double | 50 | Lower cutoff (Hz) |
| 3 | CutoffHigh | double | 150 | Upper cutoff; Bandpass/Bandstop only |
| 4 | Order | int | 4 | IIR order |
| 5 | FirTaps | int | 64 | FIR tap count |

The design rate is `SignalRate > 0 ? SignalRate : DesiredRate > 0 ? DesiredRate : 1000` — no param sets it.

## Requirements and use

Apply commits related filter settings together, between processing operations. Invalid
settings leave the previous settings active and show a card error. If filtering fails
while processing an input, that input is skipped and the error is reported; raw input
is not silently substituted. Turning filtering off explicitly still enables bypass.

Select the filter family, order and cutoff values for the true signal sample rate. Check response after changing rates at runtime: OnSignalRateChanged refreshes visualization, while OnDesiredRateChanged requests coefficient rebuilding.

For parameter positions and connection rules, see [Pipeline Configuration](../configuration.md).
For capturing output, see [Recording](../recording.md).

## Example

Follow [Signal Processing Walkthrough](../signal-lab.md) for a complete low-pass → window → MAV/FFT pipeline.
The 32 Hz cutoff preserves an 8 Hz sine at a 256 samples/s design rate. Keep this
stateful filter before overlapping window assembly so retained samples are not filtered again.

## Implementation

[API reference](xref:MOSAIC.Models.SignalProcessing.Filter). Model defaults above describe configuration loading; palette hints may expose a subset or select different defaults.
