# Drag-and-Drop Implementation

This guide explains how a block becomes draggable in MOSAIC's GUI: dragging it out of the
palette and dropping it on the graph canvas, and moving it around once it is there. It covers
what already exists, what a new block must do to take part, and how to extend the mechanism.

All paths are relative to `MOSAIC/MOSAIC/`. Search for the method names below in your editor.
The snippets explain the shared implementation; they are not code to copy into each block.

---

This is a developer guide to palette and canvas behavior. For adding blocks as a user,
follow [Your First Pipeline](first-pipeline.md#4-add-and-connect-a-block).

## Three Kinds of Dragging

MOSAIC has three separate drag interactions. They use different mechanisms, so it matters
which one you mean.

| Interaction | Mechanism | Where |
|---|---|---|
| Palette → canvas (creates a new block) | Avalonia `DragDrop` / `DataTransfer` (OS-level drag) | `Views/Palette/BlockPaletteView.axaml.cs`, `Views/GraphCanvas.axaml.cs` |
| Moving an existing node on the canvas | Pointer capture on the node control (no `DragDrop`) | `Views/GraphCanvas.axaml.cs`, `AddNode` |
| Drawing a wire from an output port to an input port | Pointer capture, build mode only | `Views/GraphCanvas.axaml.cs`, `BeginConnection` |

The second and third work for every block automatically. Only the first one needs anything
from a block author, and even that is just registration: **a block is drag-and-droppable as
soon as it has a `BlockDescriptor` in `BlockCatalog` and a matching case in `BlockFactory`.**
There is no per-block drag code.

---

## How Palette Drag-and-Drop Works

Read this once so the checklist below makes sense.

### 1. The palette lists `BlockCatalog.All`

`ViewModels/Palette/BlockPaletteViewModel.cs` groups `BlockCatalog.All` by `Category` and
filters it by the search box. Each palette row's `DataContext` is a `BlockDescriptor`
(`Components/Factory/BlockCatalog.cs`). Nothing is hand-listed in the palette; if a
descriptor exists, the row exists.

### 2. Pressing a row starts an OS drag

`BlockPaletteView.OnScrollViewerPointerPressed` walks up from the pressed element until it
finds a control whose `DataContext` is a `BlockDescriptor`, then calls `BeginDragAsync`:

```csharp
// Views/Palette/BlockPaletteView.axaml.cs
PendingDescriptor = descriptor;                       // static slot read by the drop target
var data = new DataTransfer();
data.Add(DataTransferItem.Create(BlockKeyFormat, descriptor.TypeKey));
await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
```

Two things travel with the drag:

- **`BlockKeyFormat`** (`mosaic-block-typekey`), a string format carrying the `TypeKey`. Drop
  targets use it only to recognise "this is a palette drag".
- **`BlockPaletteView.PendingDescriptor`**, a static property holding the full descriptor. The
  drop target reads the descriptor from here, not from the `DataTransfer`. It is cleared in a
  `finally` when the drag ends.

### 3. The canvas accepts the drop

`GraphCanvas` opts in during construction (`Views/GraphCanvas.axaml.cs`):

```csharp
DragDrop.SetAllowDrop(this, true);
AddHandler(DragDrop.DragOverEvent,  OnPaletteDragOver);
AddHandler(DragDrop.DropEvent,      OnPaletteDrop);
AddHandler(DragDrop.DragLeaveEvent, OnPaletteDragLeave);
```

While the pointer is over the canvas, `OnPaletteDragOver`:

- sets `DragEffects` to `Copy` if `e.DataTransfer.Formats` contains `BlockKeyFormat`, else `None`;
- computes the landing point with `ResolveDropPosition` and draws a ghost outline there;
- sets `IsDropCompatible` on every visible vertex that the dragged type could connect with
  (`IsPaletteCompatible`), which shows the green ring in `Views/GraphVertexView.axaml`.

`ResolveDropPosition` centres the block on the cursor, snaps to a 20 px grid (`SnapGrid`), and
nudges downward in grid steps until the 132 × 45 default node rectangle (`GhostW`, `GhostH`)
no longer overlaps an existing node. The drop lands exactly where the ghost was shown.

On drop, `OnPaletteDrop` raises `BlockDescriptorDropped` with the descriptor and the resolved
canvas point. The canvas itself never creates blocks.

### 4. `MainView` creates the block

`MainView.OnBlockDescriptorDropped` (`Views/MainView.axaml.cs`) does the rest:

1. Opens `AddBlockDialog` with an `AddBlockDialogViewModel` built from the descriptor. The
   dialog proposes a unique name (display name with spaces removed) and one field per
   `ParamHint`.
2. `TryBuildModel` turns the dialog into a `JsonModel` with `Type = descriptor.TypeKey` and
   `Params` in **`ParamHint` order**, hidden params included at their current values
   (initially the defaults).
3. Calls `_factory.Create(model)`. The factory switch in `BlockFactory.ResolveRegistration` maps the
   lower-cased key to the block's static `ConfigureInput(sp, m)`.
4. Wraps the block in a `VertexViewModel` at the drop point and adds it to `Blocks` and
   `Vertices`. Inputs are not set here; the user wires them by dragging ports.

If the factory returns something that is not a `BaseBlock`, the log panel shows
"Factory returned no BaseBlock" and an error dialog appears. If the key is unknown, the
factory throws `KeyNotFoundException`, which the handler logs as
"Adding '…' from the palette failed".

### 5. Moving the node afterwards

`GraphCanvas.AddNode` attaches pointer handlers to every node control. A press arms a
possible drag; moving more than 4 px (`TH`) starts it, captures the pointer, and updates
`VertexViewModel.X`/`Y` for every selected vertex from the stored origins. A
`PropertyChanged` handler moves the control on the `Canvas` and redraws edges. Because this is
per-node and generic, no block ever has to opt in.

---

## Checklist: Making a New Block Drag-and-Droppable

Assume you have already written the block class following
[Build Your Own Block](new-block-guide.md). For the complete descriptor and parameter
contract, see [Block Catalogue Registration](block-catalogue-registration.md).

### Step 1: Give the block a static `ConfigureInput`

The factory calls `YourBlock.ConfigureInput(IServiceProvider sp, JsonModel m)`. It must read
`m.Params` **positionally**; the palette emits them in the order you declare in Step 4.

### Step 2: Override input constraints only if needed

`BaseBlock` already defaults to one input and any upstream block class. The tutorial's
GainBlock needs no constraint overrides. For a block that only accepts a Clock,
override just the allowed classes:

`BlockConstraints` (`Components/Factory/BlockConstraints.cs`) reads `MinInputs`, `MaxInputs`
and `AllowableBlocks` off an **uninitialised** instance, so the constructor never runs. That
is only safe when the overrides return literals:

```csharp
public override string[] AllowableBlocks => ["ClockBlock"];   // runtime class names
```

If a getter throws, the lookup logs a warning and falls back to the defaults
(min 1, max 1, any type). Reading a field can instead return its uninitialised value,
which is another reason to use literals. These values drive:

- the `SRC` badge in the palette (`CanBeSource` means `Min == 0`);
- the green compatible-target rings during a drag;
- whether the canvas lets a wire be drawn to the block later.

### Step 3: Add the factory case

In `Components/Factory/BlockFactory.cs`, inside `ResolveRegistration`:

```csharp
"gainblock" or "mosaic.models.signalprocessing.gainblock"
    => (typeof(GainBlock), () => GainBlock.ConfigureInput(sp, m)),
```

Keys are compared after `Trim().ToLowerInvariant()`, so write them in lower case. The extra
alias is optional and only there for saved graphs that use the full type name.

`BlockGraphBuilder` delegates to `IBlockFactory`; the switch to edit is
`BlockFactory.ResolveRegistration`.

### Step 4: Add the catalog entry

In `Components/Factory/BlockCatalog.cs`, inside `Build()`:

```csharp
new("gainblock", typeof(GainBlock), "Gain", BlockCategory.SignalProcessing,
    "Multiplies every channel by a scalar gain.",
    [new("Gain", ParamKind.Double, 1.0)]),
```

Field by field:

| Field | Rule |
|---|---|
| `TypeKey` | Must be one of the strings in your factory case. `BlockCatalog.ByKey` is case-insensitive, but keep it lower case anyway. |
| `BlockType` | The CLR type. `BlockConstraints` reflects arity off it, and `CategoryByClass` uses its `Name` to tint the node. |
| `DisplayName` | Palette label, ghost label, and the default block name (spaces stripped). |
| `Category` | One of `BlockCategory`. Determines the palette group and node colour. |
| `Description` | One line. Searched by the palette search box together with the display name. |
| `Params` | One `ParamHint` per positional param, **in the order `ConfigureInput` reads them**. Use `_none` for no params. |

`ParamKind` decides the dialog field: `Int`, `Double`, `String`, `Bool`, `Enum` (needs
`Choices`), `FilePath` (opens a picker). `VisibleWhen` / `VisibleWhenValues` hide a field
unless a controlling enum has one of the listed values; hidden fields are still emitted at
their current values (initially the defaults), so the positional layout never changes.

### Step 5: Guard desktop-only blocks

If the block depends on a desktop-only package, wrap **both** the factory case and the
catalog entry in `#if !MOSAIC_MOBILE`, exactly as `delsys`, `lsl` and `pypredictor` do.
Guarding only one of them either shows a palette row that throws on drop, or hides a block
that would have loaded fine.

### Step 6: Optionally register a custom card view

`Selector/BlockTemplateSelector.cs` maps the block instance to its card view so the block has
a UI after it lands. This is unchanged from the new-block guide.

That is the whole list. Build, run the desktop app, open the palette from the right drawer,
and drag the new row onto the canvas.

---

## Verifying It

1. Run `MOSAIC.Desktop`. Open the palette (the right drawer; `ToggleMobilePalette` in
   `MainView` also switches build mode on).
2. Type part of the display name in the search box. The row should appear under the right
   category, with an `SRC` badge only if `MinInputs` is 0.
3. Drag the row over the canvas. A blue ghost with the display name should follow the cursor
   and snap to the grid; existing nodes that could connect should show a green ring.
4. Drop. The add-block dialog should list exactly your `ParamHint`s. Confirm.
5. The node appears at the ghost position. Drag it; edges should follow.
6. Check the log panel. A silent failure at any step is logged there with the `MainView`
   source.

Touch-driven drag on Android and iOS was not verified while writing this guide. The
mobile shell opens the same palette via the `+` button, but `DragDrop.DoDragDropAsync`
behaviour on touch should be tested on the device before relying on it.

---

## Common Mistakes

- **Catalog key not in the factory.** The row shows, the dialog opens, the drop then fails with
  `KeyNotFoundException` in the log. Keep the two files in step.
- **Param order drift.** The dialog emits params in `ParamHint` order and the block reads them
  by index. A reordered hint silently feeds the wrong value. `docs/block-catalogue.md`
  records several blocks where the palette declares fewer params than `ConfigureInput` reads;
  those extra params are JSON-only and cannot be set from the palette.
- **Computed constraint getters.** A getter that reads a field can return an uninitialised
  value; one that throws triggers fallback constraints. Either can produce the wrong source
  badge or connection rules.
- **Hardware side effects in `ConfigureInput`.** The drop path calls the real factory, so a
  device block that connects in `ConfigureInput` connects the moment the dialog is confirmed.
  Connect lazily or from the card instead.
- **`MaxInputs == 0` on a block that still receives.** The palette will not let a wire be
  drawn to it even though hand-written JSON works. Declare the arity the GUI should enforce.

---

## Extending the Mechanism

### Adding another drop target

Any control can accept palette drags. Mirror what `GraphCanvas` does:

```csharp
DragDrop.SetAllowDrop(this, true);
AddHandler(DragDrop.DragOverEvent, (_, e) =>
{
    bool isBlock = e.DataTransfer.Formats.Contains(BlockPaletteView.BlockKeyFormat);
    e.DragEffects = isBlock ? DragDropEffects.Copy : DragDropEffects.None;
    e.Handled = true;
});
AddHandler(DragDrop.DropEvent, (_, e) =>
{
    if (BlockPaletteView.PendingDescriptor is not { } d) return;
    // d.TypeKey, d.BlockType, d.Params ... do something with it
    e.Handled = true;
});
```

Read the descriptor from `PendingDescriptor`; the `DataTransfer` only carries the key.

### Adding another drag source

Create a `DataTransfer` with `BlockKeyFormat`, set `BlockPaletteView.PendingDescriptor`, and
call `DragDrop.DoDragDropAsync` from a `PointerPressed` handler. `PendingDescriptor` has a
private setter today; if a second source is needed, widen it or move the slot to a shared
static, and keep the `try/finally` that clears it.

### Changing drop placement

`SnapGrid`, `GhostW` and `GhostH` at the top of the palette region in `GraphCanvas.axaml.cs`
control snapping and the overlap test. `ResolveDropPosition` is the single function both the
ghost preview and the real drop use, so changing it keeps them consistent.

### Dropping with inputs pre-wired

`MainView.OnBlockDescriptorDropped` adds the vertex with no neighbours on purpose. To connect
it to the node it was dropped onto, hit-test `e.CanvasPosition` against `Vertices`, then reuse
the same three steps `OnConnectionCreated` performs: `AddSubscriber`, `Neighbors.Add`, and
updating the persisted `Inputs` list.
