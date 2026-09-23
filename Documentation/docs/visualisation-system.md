# Custom Chart ViewModels

Use this pattern for projections, training metrics and other displays that the standard
[VisualizationPanel](visualization-panel.md) does not cover.

If you only want a Scope, Spider or Heatmap, use that panel first. This page is for
writing a new kind of chart. A ViewModel is the class that holds the values displayed
by a view; here it also collects incoming results until the next screen update.

## Separate receipt from display

A custom ViewModel can implement `ISubscriber`, subscribe to a model's output, and
buffer the values it needs. Its receive method runs on a publisher's pump, not necessarily
the UI thread. Multiple upstream subscriptions can overlap.

```text
Model publishes → ReceiveInput validates and buffers
Shared UI timer → drain a snapshot → update chart state
```

Keep receipt quick. Copy values you need into a bounded display buffer or a latest-frame
slot when only the latest frame matters. Choose that policy explicitly: discarding display
history may be acceptable, while discarding training samples may not be.

## Update chart state safely

The current `VisualizationTimer` uses Avalonia's `DispatcherTimer`: its callbacks
run on the UI thread. `Subscribe(callback)` uses the default 30-fps group; the overload
also accepts `VisualizationTimer.TickRate.Fps60`. There is no background-dispatch option.
Register and unregister from the view's UI lifecycle. Keep the callback short so controls
remain responsive; the timer is a display schedule, not a data-acquisition clock.

Inside your ViewModel, retain one callback and register it when the owning view attaches:

```csharp
// Integration excerpt: UpdateChart is your method that drains the display buffer.
VisualizationTimer.Instance.Subscribe(UpdateChart);
```

Add `using MOSAIC.Services;`. Use the same `UpdateChart` delegate for removal below.
Your model's `ReceiveInput` callback still runs on the data-delivery path and should
only validate and buffer data, rather than directly changing chart controls.

Take a consistent snapshot under the buffer lock, release that lock, then update chart-bound
collections on the UI thread. Use the synchronization object expected by the chart when
its renderer reads the same state. Batch updates and cap retained points to keep long
sessions responsive.

## Manage subscriptions

Keep references to both the publisher and the exact timer delegate used for registration.
The owner must arrange matching removal:

```csharp
model.RemoveSubscriber(this);
VisualizationTimer.Instance.Unsubscribe(UpdateChart);
```

These are cleanup excerpts; names refer to the publisher and delegate retained by your
ViewModel. See existing chart ViewModels for their complete constructor and attachment pattern.

Disposal must prevent further buffering and tolerate already queued callbacks. A queued
UI callback should recheck whether the object has been disposed before touching chart state.
Do not assume that implementing `IDisposable` makes the view call it automatically.

## Choose chart data deliberately

| Output | Typical display |
|---|---|
| Two/three projection components | Scatter chart with bounded point history. |
| Per-class scores | Labelled bars or a probability display. |
| A metric over time | A line chart with an explicit time axis and history limit. |
| A spatial grid | Heatmap with known row/column meaning. |

Separate training state from display state. Hiding a chart should not silently alter
training. Model ownership, capture commands and chart attachment should have distinct roles.

## Checklist

- Validate payload type, dimensions and finite values.
- Handle concurrency from multiple publishers and UI controls.
- Bound display memory and avoid per-sample UI dispatch.
- Remove publisher and timer subscriptions at final teardown.
- Test repeated open/close and shared pop-out lifetimes.
- Reuse the standard monitors when they already meet the need.

For model/ViewModel boundaries, see [MVVM Pattern](mvvm-pattern.md). For delivery guarantees,
see [Core Architecture](core-architecture.md#delivery-and-threading).
