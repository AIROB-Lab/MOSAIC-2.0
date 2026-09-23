# API Reference

Start with the block you are working on. Under **Blocks** in the sidebar, each block
brings together its **model**, **view model**, **view**, and **related types**. Blocks
are grouped using the same categories as the Block Catalogue and sorted by name.

Opening any of these classes expands the block's section, so you can move between
its processing code and UI without searching through separate namespaces. Use
**Find a block or API type** to search all sections, including collapsed ones.

Each documented block's model page includes a **JSON configuration** section beside
its description. Copy the block entry into a larger pipeline, adjust input names and
device settings, and follow the parameter-reference link for its configuration rules.

## Example: work on the Filter block

Open **Blocks → Signal Processing → Filter** to find:

| Role | API | What to look for |
|---|---|---|
| Model | [Filter](xref:MOSAIC.Models.SignalProcessing.Filter) | Signal processing, state, and parameters. |
| View model | [FilterViewModel](xref:MOSAIC.ViewModels.SignalProcessing.FilterViewModel) | Settings and commands exposed to the UI. |
| View | [FilterCardView](xref:MOSAIC.Views.Cards.SignalProcessing.FilterCardView) | The block's card and controls. |

Not every block needs all three layers. Some cards bind directly to the model, and
some operations have no dedicated card. The grouping describes associated source
types; the card's bindings and `BlockTemplateSelector` determine what the running UI uses.

For a list of operations to use in an experiment, see the
[Block Catalogue](../docs/block-catalogue.md).

## Shared infrastructure

**Shared APIs** contains the code used across blocks: runtime contracts, factories,
plotting infrastructure, learning algorithms, connection services, and workbench UI.

| Type | What it explains |
|---|---|
| [BaseBlock](xref:MOSAIC.Components.Basics.BaseBlock) | The common contract for receiving and publishing data. |
| [OutputDispatcher](xref:MOSAIC.Components.Basics.OutputDispatcher) | Subscriber delivery and threading. |
| [BlockFactory](xref:MOSAIC.Components.Factory.BlockFactory) | How a saved type key becomes a block. |
| [BlockCatalog](xref:MOSAIC.Components.Factory.BlockCatalog) | Palette descriptions and parameter hints. |
| [JsonModel](xref:MOSAIC.Components.Basics.JsonModel) | The configuration of a pipeline block. |

## What this reference covers

The reference is generated from the shared application project at `net10.0`. It
includes private members for implementation reference as well as the public API.
The test and platform launcher projects are outside this extraction. Optional
integrations disabled in the public build, such as Delsys, do not appear in the
generated API reference.

For a worked example, follow [Build Your Own Block](../docs/new-block-guide.md).
For the runtime concepts behind these signatures, read
[Core Architecture](../docs/core-architecture.md).
