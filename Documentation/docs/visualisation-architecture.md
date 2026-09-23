# Visualization Overview

Choose the visualization path before adding chart code. Most processing blocks can use
the existing monitors; learning blocks sometimes need custom projections or metrics.

For using and interpreting the controls, start with [Reading the plots](reading-plots.md).
This page covers implementation. [Advanced block examples](advanced-blocks.md) show
sample, packet and feature rate declarations.

## Choose a path

| Need | Approach | Guide |
|---|---|---|
| Signal traces, radar values or a heatmap | `BlockVisualization` hosted by `VisualizationPanel` | [Panel Integration](visualization-panel.md) |
| A specialized chart driven by model output | A subscribing ViewModel that buffers data and updates charts on the UI thread | [Custom Chart ViewModels](visualisation-system.md) |
| Frequency history | The existing FFT/Spectrogram implementation | [FFT block](blocks/fft.md) |

## The standard monitor path

```text
Block.OnReceive
  → process a stable output
  → Publish(output)
  → BlockVisualization.Feed(output)
  → monitor buffering
  → scheduled UI update
  → chart
```

The bundle owns Scope, Spider and Heatmap monitors, creating each one only when its
property is first requested. With the panel's `Source` binding, showing a chart requests
its monitor; hidden chart types stay uncreated. The panel provides toggle buttons, size
controls and desktop pop-out behavior; the hosted monitor views add their own settings flyouts. A hidden monitor does not stop the
underlying block's processing. Feeds still track their data shape and timing while hidden,
so opening a Scope gives it the correct sample period.

## Ownership and timing

The block commonly owns the bundle, but ownership can live in a ViewModel if its lifetime
is managed explicitly. There must be one owner responsible for final disposal.
The panel manages visibility and pause/resume; it does not replace that owner.

The shared `VisualizationTimer` uses one Avalonia UI-thread `DispatcherTimer` for
its nominal 30/60-fps chart groups. It starts and stops with chart subscriptions.
Publication-based block status uses one separate shared `BlockStatusTimer`, refreshing
all blocks every 250 ms. `BaseBlock` registers and unregisters automatically; new blocks
do not create their own status-monitoring code. Suspending charts does not suspend status updates.

Processing callbacks arrive separately through the pipeline and should feed buffers
rather than mutate chart collections. Keep UI timer callbacks short and use the chart's
synchronization convention. These are UI refresh targets, not guaranteed real-time deadlines.

## Rates and shapes

Wire the model's protected `Visualization` property so `BaseBlock` can propagate its
publish rate. If the bundle is assigned after construction, refresh its rate after assignment.
For batched input, distinguish publication rate from sample rate.

Vectors typically represent channel values at one instant. Matrices can represent windows,
images or packets; choose the monitor/feed method that matches their meaning.
A heatmap is not automatically the right interpretation of every matrix.

## Resources and performance

Use the existing buffering and scheduling instead of adding one timer per block.
Bound custom display histories and avoid allocating a chart object for every sample.
Do not mutate an array or vector while another thread is still using it.

Temporary detach should pause the visible consumer; final teardown must release the
bundle or remove the custom subscriber and timer callbacks. Pop-out views can share
resources, so closing one view must not dispose resources still used by another.

See [Panel Integration](visualization-panel.md) for the implementation checklist.
See [Performance](performance.md) for batch-size tradeoffs and repeatable measurements.
