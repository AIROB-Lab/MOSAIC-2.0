# Block Catalogue Registration

`BlockCatalog.cs` describes blocks to the editor. It supplies the palette labels,
searchable descriptions, categories, and parameter fields shown when adding a block.
It does not construct blocks or implement processing.

Use this page with [Build Your Own Block](new-block-guide.md). For the parameters
of existing blocks, see the [Block Catalogue](block-catalogue.md).

If you are adding your first block, complete the Gain walkthrough first. This page
is the reference for the palette entry you add there. A **descriptor** is simply one
record containing the display name, description and parameter fields. A **parameter
hint** tells the add-block dialog which control to show, such as a numeric field.

## From a palette row to a running block

1. `BlockCatalog.All` supplies a `BlockDescriptor` for each available palette entry.
2. The palette groups descriptors by category and filters display names and descriptions.
3. Dropping a descriptor opens the add-block dialog.
4. The dialog constructs a `JsonModel` using `TypeKey` and the ordered parameter fields.
5. `BlockFactory.Create` constructs the block and connects recording support.
6. The canvas adds the instance. The user wires its ports separately.
7. `BlockTemplateSelector` chooses a card when a custom card is registered.

A catalogue entry without a factory case can appear in the palette but fail on creation.
A factory case without a catalogue entry can load from JSON but has no palette row.

## The descriptor, field by field

Add entries inside `Components/Factory/BlockCatalog.cs`, in `Build()`:

```csharp
new("gainblock", typeof(GainBlock), "Gain", BlockCategory.SignalProcessing,
    "Scales every channel of a vector by a gain; preserves channel count.",
    [new("Gain", ParamKind.Double, 1.0)]),
```

| Field | Meaning | What to check |
|---|---|---|
| TypeKey | Stable key written to the new block's JSON | Must be accepted by `BlockFactory`; keep it unique |
| BlockType | Actual C# class | Used for reflected input constraints and class-to-category lookup |
| DisplayName | Friendly palette label | Clear, short, and useful as the basis of an instance name |
| Category | Palette group and node colour | Use the existing category that fits the operation |
| Description | Searchable explanation of the block | Describe the operation, payload, and a useful limitation |
| Params | Ordered `ParamHint` list | Same indexes and defaults as the reader and exporter |

Available categories are `Analytics`, `Devices`, `FlowControl`,
`MachineLearning`, `SignalProcessing`, `Streaming`, and `Tests`.
For no parameters, use the existing `_none` array.

`ByKey` uses case-insensitive keys; duplicate keys can fail catalogue initialization.
Factory aliases need not each have a separate palette row. Choose one canonical key
for the row and keep legacy aliases in the factory for saved graphs.

## Write a useful description

A useful description answers: **What does this block do to what data?**
Add the most important prerequisite or limitation if it changes how someone uses it.

| Too vague | More useful |
|---|---|
| “Gain block.” | “Scales every channel of a vector by a gain; preserves channel count.” |
| “Device interface.” | “Reads analogue channels from the board over a serial port; requires a Clock input.” |
| “Calculates features.” | “Computes one RMS value per channel from an input window.” |

Use terminology people will search for, such as RMS, vector, serial, or ultrasound.
Describe actual behavior implemented by the model. A catalogue description does not
validate payloads, document every parameter, or replace the longer reference entry.

When adding a block, update all three documentation surfaces:

- The catalogue's short description, for discovering it in the app.
- XML comments on the model, for the generated API reference.
- A page under `Documentation/docs/blocks/` for setup and usage, plus its row in
  `Documentation/docs/block-catalogue.md`.

## Parameters are an ordered contract

A `ParamHint` has this shape:

```csharp
new ParamHint(
    Name: "Gain",
    Kind: ParamKind.Double,
    Default: 1.0)
```

The label is for humans. The model receives `Params[0]`, not a dictionary entry
called “Gain”. Keep four things aligned: the catalogue hint, `ConfigureInput`,
`GetJsonParams`, and the reference documentation.

| Kind | Use for | Value emitted by the dialog |
|---|---|---|
| Int | Counts or indexes | Integer |
| Double | Gains, rates, thresholds | Double, parsed using invariant culture |
| String | Free text | Trimmed string |
| Bool | Boolean settings | Boolean |
| Enum | A fixed list of options | String; provide `Choices` |
| FilePath | A file or path setting | String |

The current dialog parses invalid integers/doubles as zero and recognizes “true”
case-insensitively for booleans. Hints have no min/max validation fields. Validate
meaningful ranges in the model as well, since JSON can bypass the dialog.

Prefer defaults that work unchanged and match the model's missing-parameter defaults.
Do not reorder existing positional parameters: that changes the meaning of saved graphs.
Append optional parameters and handle older, shorter arrays.

### Conditional fields

For a hypothetical model whose reader expects `[Mode, Lower, Upper]`:

```csharp
[new("Mode", ParamKind.Enum, "pass", ["pass", "clip"]),
 new("Lower", ParamKind.Double, -1.0,
     VisibleWhen: "Mode", VisibleWhenValues: ["clip"]),
 new("Upper", ParamKind.Double, 1.0,
     VisibleWhen: "Mode", VisibleWhenValues: ["clip"])]
```

The controlling name must match the other hint's `Name`. Visibility only changes
the dialog: hidden fields **still occupy their positions**. The current implementation
emits their current values, initially populated from defaults; switching modes does
not reset a value the user already entered. The model must decide whether to use
those values in each mode.

## Connections and platform availability

Input counts and allowed upstream classes come from the model's `MinInputs`,
`MaxInputs`, and `AllowableBlocks`, through `BlockConstraints`.
Do not duplicate them in the descriptor. `CanBeSource` is derived from
`MinInputs == 0`; it controls the source badge.

Keep constraint getters independent of instance state because the constructor does
not run when the catalogue inspects them. Allowed upstream names are runtime class
names in the constraint lookup, not friendly palette labels.

If a block is conditional, apply the **same applicable compile condition** to its
factory case, catalogue entry, and card registration/imports. For example, desktop-only
types may use `#if !MOSAIC_MOBILE`; optional Delsys support uses `ENABLE_DELSYS`. Follow the actual
type's build conditions rather than applying one blanket guard to every device.

## Add the human-readable reference entry

Create `Documentation/docs/blocks/your-block.md`. Its structure is checked by
`Documentation/check_docs.py`, so keep the **Type** row and the three `##` headings
exactly as written here. Copy the shipped [Negate page](blocks/negate.md) as a start:

```markdown
# Your Block

[Block Catalogue](../block-catalogue.md) / Signal Processing

Explain what problem it solves and when to use it.

## Input and output

| Property | Value |
|---|---|
| **Type** | `yourblock`, `mosaic.models.signalprocessing.yourblock` |
| **Inputs** | one |
| **Consumes** | `Vector<double>` |
| **Publishes** | a new `Vector<double>` of the same length |

## Parameters

| Index | Parameter | Type | Default | Meaning / valid range |
|---|---|---|---|---|
| 0 | Example | double | 1.0 | Explain the effect and limits |

## Requirements and use

Startup steps, expected output, unsupported-input behavior, and any platform
or hardware requirements.

## Example

A complete working JSON entry, or a link to a downloadable pipeline.

## Implementation

[API reference](xref:MOSAIC.Models.SignalProcessing.YourBlock).
```

The rules the tooling enforces:

- The **Type** row must list, in backticks, every key the factory accepts for this
  block. `check_docs.py` fails when a factory key has no page or a page names a key
  the factory does not accept.
- The headings `## Input and output`, `## Parameters` and `## Requirements and use`
  must be present.
- The `[API reference](xref:...)` link must point at the model class. The build's
  `generate_api_blocks.py` uses it to attach your page to the block's entry in the
  API sidebar. A block page without it fails `check_docs.py`.
- The model class needs a class-level `<example>` comment with a
  `<code language="json">` block containing `"Type"`. `check_docs.py` fails when the
  built API page has no JSON configuration section.

Then add one row to the matching category table in `Documentation/docs/block-catalogue.md`.
The `<span id>` marks the row as a block row for the catalogue's search filter and gives
it an anchor; by convention it is the page's file name:

```markdown
| <span id="your-block"></span>[Your Block](blocks/your-block.md) | One-line purpose. |
```

Run `python Documentation/check_docs.py` from the repository root after a documentation
build to confirm the page is complete.

Copy parameter semantics from the model, then check the palette hints agree.
Do not list the tutorial's GainBlock as a shipped block until it is implemented.

## Verify a new entry

Search using both the display name and a word from the description. Confirm the
category, source badge, parameter order, defaults, and conditional fields. Drop the
block, connect it, and check the resulting behavior. Then save and reload the graph
and confirm the same parameter values return.

For drag handling and canvas behavior, see [Drag-and-Drop Blocks](drag-and-drop-blocks.md).
