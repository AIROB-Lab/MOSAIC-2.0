# Build Your Own Block

A **block** is one operation in a MOSAIC pipeline. It receives data from the block
connected before it, does some work, and sends a result to the blocks connected after it.
In this tutorial you will add a block called **Gain** that multiplies numbers.

With a gain of 2.5, the input `[1, -2, 0]` becomes `[2.5, -5, 0]`. The original input
stays unchanged. MOSAIC already has a Function block that can multiply; we use this
small operation here so you can concentrate on how to add your own code.

## Before you begin

You need basic C# knowledge (classes, methods and properties), but no previous
experience writing MOSAIC blocks. You also need a source checkout and a working
.NET 10 development setup. Follow [Getting Started](getting-started.md) to run the app.
If you have never used MOSAIC, first run [Your First Pipeline](first-pipeline.md): it
shows how to open a configuration, start the Clock and find a plot.

These words will appear throughout the tutorial:

| Word | Meaning here |
|---|---|
| Pipeline | Connected blocks that pass data from one operation to the next. |
| Model | The C# class that performs a block's work. |
| Vector | A list of channel values at one instant, such as `[1, -2, 0]`. The C# type is `Vector<double>`. |
| Palette | The searchable list of blocks you can drag into a pipeline. |
| Card | A block's controls and plots in the workbench. |
| JSON configuration | A text file describing which blocks to create, their settings and their connections. |

The **repository root** is the outer folder containing `README.md`, `Documentation`
and `MOSAIC`. Your block's source files belong inside **`MOSAIC/MOSAIC/`**.
All C# and AXAML paths below are relative to that shared application folder. AXAML
is the markup used to lay out Avalonia controls, much like HTML lays out a web page.

You will make three required edits: add the model, register how to create it, and
add its palette description. Step 5 runs a complete example and displays the result
using an existing card. You can stop there. Steps 6 and 7 add your own controls and plots.
The teaching Gain block is not shipped as a registered block; you add it during this tutorial.

## 1. Understand the pieces

| Edit | File | Why it is needed |
|---|---|---|
| Add the processing model | `Models/SignalProcessing/GainBlock.cs` | Defines what happens to incoming numbers. |
| Register construction | `Components/Factory/BlockFactory.cs` | Lets MOSAIC create your class when JSON says `"Type": "gainblock"`. |
| Add the palette entry | `Components/Factory/BlockCatalog.cs` | Gives the block a name, description and Gain field in the add-block dialog. |

Think of the factory as the list of instructions for creating blocks, and the
catalogue as the list shown to users. Adding a class alone does not add it to either list.
You will edit each file in order below; no changes to the graph builder are needed.

There are three names to keep track of:

- `GainBlock` is the C# class you create.
- `gainblock` is the type key stored in JSON and used by the factory and catalogue.
- `Gain` is its palette label. A particular instance can have its own name, such as `Amplify`.

For example, a block named `Amplify` with `"Type": "gainblock"` is one instance of
`GainBlock`. Its name is how other blocks refer to it in their `Inputs` lists.

MOSAIC already handles connections, delivery of data and recording. A normal
one-input block inherits those features from `BaseBlock`; you only write your operation
and its settings. A ViewModel and card are optional and come later.

## 2. Write the processing model

Download [GainBlock.cs](../examples/GainBlock.cs) and copy it to
`Models/SignalProcessing/GainBlock.cs`. The code displayed below is included directly
from that file, which is compiled by the documentation tests:

[!code-csharp[GainBlock](../examples/GainBlock.cs)]

The class is grouped into four sections: **Settings** holds the gain and its validation;
**Construction** sets its starting value; **Processing** contains the operation;
**JSON configuration** loads and saves the settings. The `#region` markers let you
collapse these sections in your editor; they do not change how the code runs.

### Read the example from input to output

Each time data arrives, MOSAIC calls `OnReceive(sender, value)`. `sender` identifies
the upstream block; **`value` contains the data**. Follow these steps in the method:

1. Check that `value` is a vector. An unexpected type is rejected without publishing.
2. Read the current gain once, so all channels use the same setting.
3. Create a new vector with `input.Multiply(gain)`.
4. Call `Publish(output)` to send that result downstream. Recording, when enabled,
   also receives the published result.

An unexpected payload calls `ReportError` and publishes nothing. The next valid vector
calls `ClearError` before processing, which is the current shared diagnostic pattern.

**Leave `Status` to `BaseBlock`.** It updates status and the displayed frequency
automatically from publication activity and the expected rate. You do not need to
set `BlockStatus.Normal` after processing or `BlockStatus.Stumbling` when rejecting
an input. In this example an unsupported input simply produces no output. The status
indicator reports runtime activity; it is not a validation result for each value.

The remaining members connect that operation to settings and saved files:

| Member | Why it is in this example |
|---|---|
| Constructor | Receives the instance name, rate and starting gain. |
| `Gain` / `SetGain` | Read the setting and apply a new finite value. A finite value excludes NaN and infinity. |
| `ConfigureInput` | Reads `Params[0]` from JSON and creates the instance. If no gain is supplied, it uses 1.0. |
| `JsonTypeName` | Saves the stable key `gainblock`, so the same type can be loaded again. |
| `GetJsonParams` | Saves the current gain back into the first parameter position. |

C# lists start at index zero: `Params[0]` means the **first** value. The base class
already exports the common fields such as name, input connections and rate.
For a block without settings, you can leave `GetJsonParams` out entirely.

`IServiceProvider` supplies application services to constructors that need them.
`ActivatorUtilities.CreateInstance` combines those services with the explicit values
passed here. Gain has no extra service dependencies; keep this construction pattern
when adapting the example.

**Checkpoint:** the new file declares `GainBlock` in `MOSAIC.Models.SignalProcessing`.
It does not appear in the palette yet; the next two edits make MOSAIC aware of it.

## 3. Register construction in BlockFactory

In `Components/Factory/BlockFactory.cs`, find the `ResolveRegistration` method and its list of type keys. Add this case before
the final `_ => throw UnknownBlockType(m)` line:

```csharp
"gainblock" or "mosaic.models.signalprocessing.gainblock"
    => (typeof(GainBlock), () => GainBlock.ConfigureInput(sp, m)),
```

The existing file imports `MOSAIC.Models.SignalProcessing`; add that import if it
is absent in your checkout. The factory trims and lowercases incoming type keys.
Keep the exported `JsonTypeName` supported even if you later change the display name.

The text on the left lists accepted JSON type keys. On the right, `typeof(GainBlock)`
lets the loader check connections before constructing the block. The function after it
calls your `ConfigureInput` method when construction is needed. The full class-name alias is optional; the short
`gainblock` key is the one used throughout this tutorial.

**Checkpoint:** MOSAIC can now create the block from JSON after rebuilding.
The palette entry is the next edit.

## 4. Add the catalogue description and parameters

In `Components/Factory/BlockCatalog.cs`, add this entry inside `Build()`,
alongside the other signal-processing blocks:

```csharp
new("gainblock", typeof(GainBlock), "Gain", BlockCategory.SignalProcessing,
    "Scales every channel of a vector by a gain; preserves channel count.",
    [new("Gain", ParamKind.Double, 1.0)]),
```

The entry says: use the key `gainblock`, create a `GainBlock`, show the label **Gain**
in **Signal Processing**, and offer one numeric field with a default of 1.0.
The description helps users find and understand it. `typeof(GainBlock)` refers to
your C# class; the key must match the factory case you just added.

This catalogue entry is also the complete drag-and-drop registration. The palette reads
`BlockCatalog.All`, and the canvas uses generic handlers for every descriptor. Do not add
a Gain-specific row to palette AXAML or a Gain-specific drag handler to `GraphCanvas`.

For this example, there is exactly **one parameter**:

| Index | Dialog label | Kind | Default | Read by | Exported from |
|---|---|---|---|---|---|
| 0 | Gain | Double | 1.0 | `ConfigureInput` | `Gain` |

Keep the dialog field, JSON reader and exporter in the same order. A label in the
dialog does not implement the behavior; `SetGain` and `OnReceive` do that work.

[Block Catalogue Registration](block-catalogue-registration.md) explains every
descriptor field, conditional parameters, descriptions, and platform guards.

## 5. Try the block in a pipeline

### Build and start your edited application

Close the running desktop app before rebuilding. Open a terminal at the repository
root and enter the solution folder once:

```text
cd MOSAIC
dotnet run --project MOSAIC.Desktop/MOSAIC.Desktop.csproj -f net10.0
```

`dotnet run` builds the edited source and starts the application. The generic desktop
target is enough for this hardware-free example. If you already use a platform target
from [Getting Started](getting-started.md), you can keep using that build/run command.
A build error mentioning `GainBlock` usually means the file, namespace or registration
needs checking; fix the first error before opening the pipeline.

### Load a complete example

[Download gain-pipeline.json](../examples/developer/gain-pipeline.json), or save the
JSON below in a new text file called `gain-pipeline.json` (not `.json.txt`).
Open it in MOSAIC with **File → Open** or the folder button.
It will load only after you have added the model and factory case above.

```text
Clock → Signal → Amplify → Check
                 your      existing Function block
                 block     used to display its output
```

[!code-json[Gain pipeline](../examples/developer/gain-pipeline.json)]

| Block | What this configuration asks it to do |
|---|---|
| `Clock` | Send 100 ticks per second. |
| `Signal` | Generate one channel: amplitude 1, frequency 1 Hz, one sample per tick. Its parameters are component count, amplitude, frequency, phase, phase step and scans per packet. |
| `Amplify` | Apply your gain of 2.5 to each received value. |
| `Check` | Use the existing Function block to multiply by 1, leaving the result unchanged. Its existing card lets you plot the result. |

The Clock's 100 ticks/s controls sample delivery. The sine's 1 Hz means one full
wave cycle per second. These are different settings.

### See whether it works

1. Open the block controls on the left and press **Play** on the Clock card.
2. Open **Signal** and show its **Scope**. Its values should range approximately from -1 to 1.
3. Open **Check** and show its Scope. Its values should range approximately from -2.5 to 2.5,
   with the same one-second cycle. Compare the axis numbers: automatic scaling can
   make different amplitudes look equally tall.
4. Pause the Clock. Change Amplify's JSON `Params` to `[0]`, reopen the file and press Play.
   Check should now show zero. Restore `[2.5]` afterwards.

Amplify itself shows the class name until you add a custom card. That is expected;
its processing works independently of its display. The Check block lets you confirm
this before doing any UI programming.

### Check the palette too

Pause the Clock, open the right-hand palette and search for **Gain**. Drag it onto
the canvas, choose a different instance name, enter the gain and confirm. Connect
Signal's bottom output port to the new block's top input port in build mode.
The add-block dialog creates the block; you draw its connections afterwards.

**Checkpoint:** you have implemented, registered and run your own block. You can
stop here and use JSON or the add-block dialog to choose its starting gain.
Continue only if you want to adjust that gain from a card while the app is open.

## 6. Add an optional control card

The card has a slider and an **Apply** button. It needs a **ViewModel**, a small
class holding the values and commands shown by the controls. The model continues
to do the processing. Moving the slider changes a draft value; pressing Apply sends
that value to the model. This keeps a partially edited setting from taking effect.

You will add three files and one selector case. Do not put processing code in these files.

Create `ViewModels/SignalProcessing/GainViewModel.cs`:

Download [GainViewModel.cs](../examples/GainViewModel.cs) and copy it to the path above.

[!code-csharp[GainViewModel.cs](../examples/GainViewModel.cs)]

The attributes above generate the bindable `Gain` and `Message` properties and
`ApplyCommand` during compilation. Keep the class `partial`; do not write the
generated members yourself.

Create `Views/Cards/SignalProcessing/GainCardView.axaml`:

Download [GainCardView.axaml](../examples/GainCardView.axaml) and copy it to the path above.

[!code-xml[GainCardView.axaml](../examples/GainCardView.axaml)]

The slider deliberately offers a small editing range; the model and JSON accept any
finite gain. Choose controls and limits that match your own block's requirements.

Create `Views/Cards/SignalProcessing/GainCardView.axaml.cs`:

Download [GainCardView.axaml.cs](../examples/GainCardView.axaml.cs) and copy it to the path above.

[!code-csharp[GainCardView.axaml.cs](../examples/GainCardView.axaml.cs)]

`{Binding Gain}` connects a control to the ViewModel's Gain property. `TwoWay`
allows the slider to change it. `DataContext` (assigned next) supplies the ViewModel,
and `x:DataType` lets the compiler check the binding names.

Follow the existing cards: the AXAML root is `cards:PopoutCardBase`, and the code-behind
inherits `PopoutCardBase`. The example also uses the shared `card`, `cardSection`, status
converter, category colour, and `SafeNumericUpDown` patterns used by current cards.

Finally, open `Selector/BlockTemplateSelector.cs` and add this arm to
`BlockTemplateSelector.CreateCard` before the fallback:

```csharp
GainBlock block => new GainCardView
{
    DataContext = block.GetOrCreateOwned(() => new GainViewModel(block))
},
```

Add these imports to the selector if they are not already present:

```csharp
using MOSAIC.Models.SignalProcessing;
using MOSAIC.ViewModels.SignalProcessing;
using MOSAIC.Views.Cards.SignalProcessing;
```

Build and run the application again using the command in step 5. Then
open the block, move the slider, and press Apply. Save and reload the pipeline:
`GetJsonParams` should preserve the **applied** gain.

The MVVM toolkit generates the `Gain` and `Message` properties, their change
notifications, and `ApplyCommand` from the attributes. Do not add those members
by hand. This example needs no `IDisposable` implementation because it owns no
subscriptions, timers or visualization resources.

## 7. Add visualization only when needed

The minimal block above intentionally owns no visualization. If you add one:

1. Decide whether the model or ViewModel owns the `BlockVisualization`.
2. Expose it to `VisualizationPanel.Source` in the card.
3. Override the model's protected `Visualization` property so `BaseBlock` can
   propagate the publish rate automatically. For this one-vector-per-input gain
   block, that supplies the Scope time base without per-sample rate calls.
   Call `RefreshVisualizationRate()` after assigning
   a visualization later in the lifecycle.
4. Feed output after `Publish(output)`. Do not update chart controls directly from
   `OnReceive`.
5. Dispose the bundle in its owner. `BaseBlock.Dispose` does **not** dispose
   an arbitrary `Viz` property you added. If you override `Dispose(bool)`,
   keep cleanup idempotent and call the base implementation.

Use [Visualization Architecture](visualisation-architecture.md) and
[Panel Integration](visualization-panel.md) for the complete monitor pattern.
The panel handles monitor visibility and pause/resume; no custom UI timer or
manual `Scope.Resume()` is needed. `Models/Templates/TemplateViz.cs` shows the same
bundle pattern without gain controls; it is a source template, not a registered palette block.
If your ViewModel subscribes to events or owns timers, implement `IDisposable` and
unsubscribe or stop them there. The selector uses `GetOrCreateOwned` to reuse one
ViewModel for each block and dispose it with that block. Closing a pop-out or
regrouping cards does not dispose this shared ViewModel. `PipelineSession` owns
the blocks and awaits their cleanup on deletion, reload and desktop exit.

## 8. Verify the whole contract

| Check | Expected result |
|---|---|
| Transform `[1, -2, 0]` with gain 2.5 | Output `[2.5, -5, 0]`; input remains unchanged |
| Gain 0, 1, and -1 | Zeros, unchanged values, and sign inversion respectively |
| Unsupported payload, such as a matrix | No output; `LastError` explains that Gain requires a numeric vector |
| Missing `Params` | Gain defaults to 1.0 |
| Palette creation | One Double field called Gain, default 1.0 |
| JSON creation | The canonical key and optional full-name alias both construct the block |
| Apply, save, reload | Applied gain survives; unapplied slider changes do not |
| Graph wiring | One input accepted; a second input rejected by the editor |
| Start/stop and remove | No block-owned workers or subscriptions left running |

Add focused automated tests for processing and configuration round-trips when you
implement your own block. Check UI behavior in the app as well: successful compilation
alone cannot confirm the palette, connection rules, or saved configuration.

The downloadable Gain and RMS models, Gain ViewModel and Gain card are compiled
against the current application by the documentation tests, including the card's
AXAML bindings. Tests also check that Apply changes the model and that saved JSON
keeps the applied value rather than an unconfirmed draft. From the repository root, run:

```text
dotnet test MOSAIC/MOSAIC.Tests/MOSAIC.Tests.csproj --filter FullyQualifiedName~DocumentationWorkflowTests
```

Then document the block in the human-readable [Block Catalogue](block-catalogue.md).
This takes two edits, not one:

1. Create a page `Documentation/docs/blocks/gain.md` with the fixed structure shown in
   [Block Catalogue Registration](block-catalogue-registration.md#add-the-human-readable-reference-entry):
   a **Type** row listing every factory key in backticks, the three required headings,
   and an `[API reference](xref:...)` link to the model class.
2. Add a row to the **Signal Processing** table in `Documentation/docs/block-catalogue.md`:

   ```markdown
   | <span id="gain"></span>[Gain](blocks/gain.md) | Scales every channel of a vector by a gain; preserves channel count. |
   ```

`check_docs.py` compares the factory's type keys with the **Type** rows of all block
pages and fails on any key that appears on only one side: a factory alias with no block
page, or a page listing a key the factory does not accept. The `xref` line is what lets
the build attach your page to the block's API entry; the check also fails if a block
page lacks it.
Rebuild the documentation using [Building the Documentation](building-documentation.md).
The full build automatically adds your block to the API sidebar and groups its model,
view model and views using the selector and naming conventions. A separate navigation
entry is unnecessary. Use the API association overrides described in that guide only
for unusual class names or helpers that live elsewhere.

Add a class-level XML `<example>` comment with a `<code language="json">` block
containing the block's JSON entry, including `Type`. This is required, not optional:
`check_docs.py` fails when a documented model's API page has no JSON configuration
section. Explain parameter positions and
any placeholder input names, device addresses or paths beside it. The documentation
theme places this example in a **JSON configuration** section beside the model
description during a normal DocFX build. Keep configuration examples on the model;
view and view-model pages describe the UI implementation.

Continue with [Batches, features and source blocks](advanced-blocks.md) for a downloadable,
tested RMS feature block, rate declarations, device lifecycle and multiple-input policies.

## When you adapt this example

The working steps above are enough for Gain. Read these details when changing its
input type, adding settings or owning resources such as a device connection.

### What MOSAIC already provides

| Already provided | What you still write |
|---|---|
| One input and no restriction on upstream block class | Override constraints only when your block differs; validate the payload in `OnReceive`. |
| Subscriptions, downstream dispatch and rate inheritance in `BaseBlock` | Call `Publish(output)`; declare different rate semantics for windowing, resampling or features. |
| Automatic status and displayed-frequency updates | Validate the input and publish valid results; do not assign `Status` in this example. |
| JSON export of name, connections, rate and recording path | Export your parameters with `GetJsonParams`; keep a stable `JsonTypeName`. |
| No parameters by default (`GetJsonParams` returns null) | No override is needed for a parameter-free block. |
| Recording setup in `BlockFactory.Create` and logging through `Publish` | Use the recording controls or JSON `Path`; no dumper setup in `ConfigureInput`. |
| Palette search, add-dialog fields, node movement and connection UI | Supply the catalogue descriptor; no block-specific drag handlers. |
| Standard plot controls and pause/resume in `VisualizationPanel` | Optionally own, feed and dispose a visualization bundle, then bind the panel. |

For an even smaller example without settings, inspect the shipped
[Negate model](xref:MOSAIC.Models.SignalProcessing.Negate). `GainBlock` adds one
parameter to demonstrate configuration and save/reload. Type discovery is **not**
automatic: the factory case and catalogue entry from steps 3 and 4 are still required.

### Processing and shared settings

`OnReceive` runs on a dispatching thread. Keep processing short and avoid UI work,
blocking network calls, or disk writes here. Other blocks may see the same input
object, so allocate your output and treat published data as immutable: do not reuse
or mutate it after `Publish`.

`Gain` is shared between the UI and processing threads. This example uses
`Volatile.Read/Write` for that one scalar. A block with several related settings
should publish a consistent settings snapshot or protect them together. Keep editable
values in the ViewModel, then validate and apply them in one model method. For example,
`Filter.UpdateParameters` commits the whole band under the same lock used by processing;
a packet cannot see a new lower cutoff paired with the previous upper cutoff.
`[ObservableProperty]` provides notifications, not this synchronization.

`ConfigureInput` constructs the block; `GetJsonParams` exports its current settings.
They must agree on positions and defaults. The JSON helpers accept the representation
used by loaded JSON as well as supported CLR values, but they can fall back silently
for malformed values. Add strict validation if silently using a default would be wrong
for your block.

Recording is already connected in `BlockFactory.Create`: it calls `InitDumper`
and `AttachRecording`. `Publish` sends output to the dumper. You do not need
another recording setup or a manual CSV write in this example.
This applies when construction goes through `BlockFactory.Create`; calling the
constructor or `ConfigureInput` directly bypasses that factory setup.

### Report errors separately from activity

`Status` describes recent output activity and is updated automatically. For an input or
processing failure, call `ReportError("Explain what went wrong and what was skipped.")`.
The card displays the message separately and the shared logger records it. Call
`ClearError()` after an explicit reset or recovery when appropriate. Publishing another
value does not erase the error. Leave `Status` updates to `BaseBlock`; do not assign it
after processing or add another activity-status property. Use a clearly named message or state property
for a separate concept such as connection state or training progress.

A filter failure skips the affected input; it does not publish raw data as if filtering
had succeeded. Intentional bypass remains available through the Filter card's enable switch.

### Input constraints are not payload validation

`GainBlock` inherits `MinInputs = 1`, `MaxInputs = 1` and an empty `AllowableBlocks`
from `BaseBlock`, so the example does not repeat those getters.
`MinInputs` and `MaxInputs` describe graph connections. An empty
`AllowableBlocks` accepts any upstream block class; it does **not** mean any
payload type is valid. The `Vector<double>` check remains necessary.

`BlockConstraints` reads these getters on an uninitialised instance. Never depend
on constructor state, hardware, or parameter fields in these getters. For a source,
use a literal `MinInputs => 0`; for a clock-driven generator, inspect the existing
`SinGenerator` constraints and lifecycle. See [Core Architecture](core-architecture.md)
for scheduling, dispatch, and source blocks.

## Common problems

| Symptom | What to check |
|---|---|
| Unknown type when loading or dropping | Factory key matches the catalogue and exported type |
| Loads from JSON but is missing from the palette | Catalogue entry and platform guards |
| Dialog accepts a field but it has no effect | Hint order matches parameter reader and exporter |
| Block receives data but emits nothing | Actual payload type and upstream activity |
| Settings appear to reset after reload | `GetJsonParams` exports current model state |
| Card is missing | Selector arm, namespaces, and placement before the fallback |
| UI stalls or falls behind | Work in `OnReceive`, per-sample UI updates, and blocking calls |
