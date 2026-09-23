# Cross-correlation block on net10migration

> This page records the original porting work and its verification results.
> For current block setup and both supported parameter formats, read
> [Cross Correlation](../docs/blocks/cross-correlation.md). For a first MOSAIC session,
> start with [Your First Pipeline](../docs/first-pipeline.md).


This is a **selective code port**, not a merge of all `origin/feature_xcorr` history.
The initial source was commit `819bb1bb02bef8cceb8615b75fd4b1eaefc56ffb` (cross-correlation
block and visualizers). The later channel/filter/peak-output updates from
`origin/feature_xcorr` at `ccbcbd1`, plus its unsigned 16-bit UDP format, were subsequently
ported for the user's four-channel UDP pipeline. All code uses the current factory,
palette, card selector and recording APIs.

No SDK selection, package versions, project files, BLE implementations or platform
startup code were taken from the .NET 8 branch. Its separate VectorialFunction block,
Matrix2Vector changes, unrelated examples and shared spectrogram zoom-slider changes
were not imported. The card uses net10migration's existing monitor implementation.

For the named `Lags:1:36` / four-channel UDP format, see
[Cross-correlation over UDP on iPad](cross-correlation-udp-ipad.md).
The legacy numeric, three-parameter format described below remains unchanged.

## Try it

Find **Cross Correlation** under **Signal Processing**, or open
`MOSAIC/MOSAIC/Assets/Examples/BlockTestFiles/CrossCorrelation.json` and start `Clock`.
That example generates two phase-shifted channels at 200 Hz; expect about 2.6 seconds
of warm-up before the first result.

The block accepts **one upstream input** carrying either:

- A matrix with samples in rows and the two signals in columns 0 and 1. Additional
  columns are ignored, matching the original block.
- An interleaved vector `[ch1_sample0, ch2_sample0, ch1_sample1, ch2_sample1, ...]`.
  A two-element vector represents one sample from each channel.

For two independent source blocks, combine their channels upstream; do not wire them
as two independent inputs to this block.

Parameters, in JSON order:

| Parameter | Default | Meaning |
| --- | --- | --- |
| MaxLag | 0 | Lags from `-MaxLag` to `+MaxLag`. Zero selects the full lag range. |
| BufferLen | 1000 | Maximum retained samples per channel; clamped to at least 511. |
| ProcessEveryN | 5 | Compute once every N accepted packets; minimum 1. |

The original processing formula is preserved: z-normalization, 10-sample moving
maximum, subtraction of a forward 500-sample mean, then cross-correlation. This is
**not** plain raw-signal/Pearson correlation. The original result scaling is retained:
L2 norm 10 for a nonzero result. A delayed second channel has its peak at negative lag.
The snapshot plots use array-bin indices; zero lag is at index `MaxLag`, or the centre
bin when using the full equal-length range.

## Compatibility and safety changes

- Factory-owned recording replaces the obsolete three-argument `InitDumper` call.
  Primary output uses `<Name>_xcorr.csv` and the current recording toggle. An explicit
  JSON `Path` also creates the original processed-channel sidecars `<Name>_ch1.csv` and
  `<Name>_ch2.csv`; these stop receiving rows when primary recording is switched off.
- Short packets accumulate with a warm-up message instead of reaching negative/empty
  array allocations in the preprocessing stages.
- The buffer limit is enforced before computing, including oversized incoming packets.
- Invalid lag/buffer/cadence settings are clamped, and disposal is idempotent and
  synchronized with receive processing.
- JSON save/reload and the newer palette are supported. Existing desktop/mobile
  registrations are retained.

Regression tests cover the original processing formula, lag direction/scaling,
matrix/vector equivalence, warm-up, oversized packets, flat input, packet cadence,
parameter bounds, save/reload, palette registration, recording and disposal.

## Verification

Verified on Windows with .NET SDK 10.0.303:

- All 1,202 tests passed, including 27 cross-correlation/UDP-pipeline regression tests.
- Desktop Debug build passed.
- Android Debug build for `android-arm64` passed.
- iOS managed compilation for `ios-arm64` passed after restoring its dependencies.
  This is not a native iOS build, signing check or iPad runtime test; those still
  require the Mac/iPad setup.

The test counts above describe that porting session, not the current checkout.
