# Developer example review — 15 September 2026

## Scope and result

Reviewed the Build Your Own Block tutorial, its Gain and RMS models, the optional
Gain ViewModel and card, and the relevant factory, catalogue, subscription,
visualization and template code. This is a focused source review and automated
check, not a certification of the entire application or its hardware integrations.

The exact downloadable model, ViewModel and card files are now compiled by
`MOSAIC/MOSAIC.Tests`, including the AXAML bindings. Tests cover transformations,
input ownership, invalid inputs, JSON round-trips, the complete Gain pipeline,
feature rates and applying a draft card setting. The example's factory and
catalogue entries still need to be added by the student; these examples are not
registered as production blocks by the tests.

## Corrections

- Removed the remaining instruction in `BaseBlock.OnReceive`'s API comment to set
  `BlockStatus.Stumbling` for invalid payloads. Status monitoring is automatic.
- Corrected `OutputDispatcher`'s concurrency explanation. Ordering is guaranteed
  per subscription; a block connected to two publishers can receive concurrent calls.
- Made Gain's optional UI files downloadable and included those same files in the
  tutorial, avoiding separately maintained code snippets. Added an Apply/save test
  and the selector's exact file location.
- Fixed duplicate rate subscriptions in `BaseBlock`. Registering the same subscriber
  twice and removing it once could leave a disconnected block receiving rate changes.
- Fixed returning a `BlockVisualization` to automatic timing. Clearing an explicit
  rate now recalculates from the latest publication rate and feed shape instead of
  using an old cached rate.
- Fixed `SlidingWindow.ConfigureInput` indexing past an empty or partial parameter
  list. Omitted entries use the existing defaults: 100 rows, stride 25, Hamming.
- Corrected a timing assumption in the BodyRig two-sensor tests. They now keep the
  pacing sensor sending until a complete pose arrives, within the existing timeout,
  instead of assuming another publisher's pump finishes during a fixed sleep.

The three runtime corrections first failed dedicated regression tests. After the
fixes, the full .NET test project passed **1,226 tests**, with no failures or skips.
Existing compiler/analyzer warnings remain; a passing test run does not clear them.
The full DocFX build completed with zero warnings and errors. All seven API
navigation tests passed, and the documentation checker validated guide links,
downloads and 70 block pages covering 131 factory aliases.

## Confirmed remaining issue

`SlidingWindow` still calculates output publication metadata using upstream
publications/s divided by stride. It appends every matrix row, so this understates
the actual output cadence for batched sources. For example, 64 packets/s containing
four rows each with stride 64 produces four windows/s, but reports one window/s.
Downstream feature scopes and expected-frequency indicators can consequently use
the wrong rate. The existing
`BatchedSource_PreservesSamplesButWindowMetadataRequiresCare` test records this
limitation; it does not assert the desired corrected behavior.

This needs a coordinated correction to sample-rate and publication-rate propagation,
including runtime rate changes and downstream window/feature blocks. The beginner
Gain pipeline and the RMS tutorial use one source sample per publication and avoid
this particular limitation. It remains documented in the Sliding Window and Data
and Time guides.

## Checks not performed

The card's code and bindings compile, but this review did not exercise its appearance,
slider interaction or pop-out behavior in a running desktop window. No physical
devices, macOS, iOS or browser targets were tested in this review.
