# MVVM Pattern

MOSAIC separates processing from the controls used to configure and display it.
Use the [Build Your Own Block](new-block-guide.md) walkthrough as the complete example;
this page explains the design choices behind its optional card.

## Responsibilities

| Layer | Responsibility |
|---|---|
| Model: a `BaseBlock` | Own processing state, validate changes, receive and publish data. |
| ViewModel | Expose settings, commands and display state for binding. |
| View | Lay out controls and plots; handle view-specific interaction and attachment. |

Processing belongs in the model. UI concerns such as dragging, opening a pop-out and
view attachment can live in the view infrastructure. Models may own visualization bundles,
but should not manipulate chart controls directly from their processing callbacks.

## Choose how settings reach the model

For a setting changed only through an Apply button, the ViewModel can hold an **edit buffer**.
Initialize it from the model, let the user edit it, then validate and apply through a command.

```csharp
[ObservableProperty] private double _gain;

[RelayCommand]
private void Apply()
{
    if (double.IsFinite(Gain))
        Block.SetGain(Gain);
}
```

This is an excerpt from a partial ViewModel using CommunityToolkit.Mvvm, with a `Block`
property referring to the model. The full walkthrough includes imports and construction.
`ObservableProperty` generates the bindable property and `RelayCommand` generates
`ApplyCommand`, which the tutorial card binds. The walkthrough's version also sets a
`Message` property to report the result.

An edit buffer is not automatically synchronized with external model changes. If the
setting can also change elsewhere, either bind directly to a suitable observable model
property or subscribe to model notifications deliberately. Marshal bound-state changes
to the UI thread when notifications arrive from processing or device threads.

## Thread safety is the model's responsibility

An Apply command can run while `OnReceive` processes a sample. Even a one-input block
must handle this overlap. Use synchronized configuration snapshots or an appropriate
atomic access pattern in the model. Generating a property with the MVVM toolkit does
not make its backing state thread-safe.

Avoid recomputing a processing result separately for publishing and plotting. Compute
once, then publish and feed the same stable result to the visualization.

## Connect the view

The card uses compiled bindings with `x:DataType` and exposes commands through bindings:

```xml
<Button Content="Apply" Command="{Binding ApplyGainCommand}" />
```

`BlockTemplateSelector` maps a block to its card and constructs the ViewModel.
A minimal block may use the existing fallback instead of defining a dedicated card.
For a new card, follow the walkthrough's `UserControl` AXAML root and
`PopoutCardBase` code-behind convention.

## Own the lifetime explicitly

A ViewModel that subscribes to a model, timer or publisher needs a matching unsubscribe.
Implement `IDisposable` for that cleanup. In `BlockTemplateSelector.CreateCard`,
use `block.GetOrCreateOwned(() => new YourViewModel(block))`. The block then owns
one shared ViewModel and calls its disposal during pipeline cleanup. Main cards,
regrouped cards and pop-outs reuse this instance. `PipelineSession` awaits block
cleanup before replacing a pipeline or completing desktop shutdown.

- Separate temporary visual detachment from final disposal.
- Avoid disposing a shared ViewModel when only one of its views closes.
- Stop callbacks and remove subscriptions before releasing owned resources.
- Make cleanup safe to call more than once.
- If queued UI work may run after disposal, check lifetime again inside that work.

See [Visualization Integration](visualization-panel.md) for model-owned plots and
[Custom Chart ViewModels](visualisation-system.md) for subscriber-owned charts.
