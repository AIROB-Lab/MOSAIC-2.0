# Visualization Panel Integration

Use `VisualizationPanel` to put the standard Scope, Spider and Heatmap monitors in a card.
For a complete block and card scaffold, start with [Build Your Own Block](new-block-guide.md).

A **bundle** is one `BlockVisualization` object holding those three monitors.
The **panel** is the visible control containing their buttons and plots. The model
feeds numbers to the bundle; the card displays the panel. These are optional additions
to a block that already processes and publishes data.

## 1. Give the bundle an owner

The following members belong inside a block that owns its visualization:

```csharp
public BlockVisualization Viz { get; } = new();
protected override BlockVisualization? Visualization => Viz;

protected override void Dispose(bool disposing)
{
    if (disposing) Viz.Dispose();
    base.Dispose(disposing);
}
```

Add `using MOSAIC.Visualization;`. This is an integration excerpt, not a standalone class.
For the Gain tutorial, put these members inside `GainBlock`, next to its `Gain` property.
If the bundle is assigned later, call `RefreshVisualizationRate()` after assigning it.
If the owner is a ViewModel instead, place final cleanup in its explicitly managed lifetime.
Both `BlockVisualization.Dispose()` and base cleanup are already idempotent, so this
example needs no extra disposed flag. Guard any additional resources you own as needed.

`BaseBlock.Dispose` does not discover and dispose an arbitrary `Viz` property.

## 2. Feed the processed result

In a vector-processing block, compute the result once:

```csharp
Publish(result);
Viz.Feed(result);
```

Here `result` means your computed output vector. In `GainBlock.OnReceive` it is named
`output`, so keep `Publish(output)` and add **`Viz.Feed(output)` immediately after it**.
Do not add another `Publish` call: that would send the result downstream twice.

Feed a stable result from the processing callback. Do not modify bound chart collections
there. Validate the input type and shape before processing; choose appropriate feed methods
for batched or image data.

Choose the rate and matrix-selection policy explicitly. [Advanced block examples](advanced-blocks.md#give-the-visualization-the-right-rate)
distinguish contiguous packets, overlapping windows and feature streams. Feeding a bundle
alone does not prove that its time axis matches acquisition time.

For one vector per publication, the `Visualization` override above is enough to
forward the model's publication rate. No per-sample `UpdateSignalRate` call is needed.
Fixed-size contiguous matrices similarly derive rows/s from publication rate and row
count. Explicit sample-rate and overlap settings are for data whose meaning cannot
be represented by that derivation.

## 3. Bind the panel

Expose the model as the ViewModel's `Block` property and add the visualization namespace:

```xml
xmlns:viz="clr-namespace:MOSAIC.Visualization"
```

Then place the panel in the card:

For Gain, add the namespace to the opening `UserControl` element and put the panel
inside its existing `StackPanel`, below the Apply button and message.

```xml
<viz:VisualizationPanel
    Source="{Binding Block.Viz}"
    ScopeVisibleByDefault="True"
    ShowScopeViewModeToggle="True"
    MinHeight="160" />
```

Use the card's `x:DataType` for compiled bindings. The `Source` supplies the bundle's monitors;
do not simultaneously bind individual monitor properties to different instances.

## Visibility and controls

The panel manages monitor pause/resume as plots are shown, hidden, attached or detached.
Use its controls for monitor selection, size, settings and supported pop-out behavior.
You normally do not call `Resume` or `Pause` manually when using this host.

Visibility management is distinct from final disposal. Coordinate teardown with the
actual owner, especially when a pop-out and card share the same bundle.

## Verify integration

1. Start the block and show Scope; confirm values and time scale.
2. Hide and reopen the plot; processing should continue and the plot should resume.
3. Change the sample/channel shape through the supported controls.
4. Exercise pop-out and return on desktop.
5. Remove or replace the pipeline and check that subscriptions and owned resources stop.

For a custom chart rather than the standard monitors, use
[Custom Chart ViewModels](visualisation-system.md).
