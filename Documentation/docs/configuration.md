# Pipeline Configuration

A pipeline is a JSON object. Each top-level key names a block; its value describes
the type, inputs, and settings. Names must match wherever another block refers to them.

## A complete example

```json
{
  "Clock": { "Type": "Clock", "DesiredRate": 200 },
  "signal": {
    "Type": "SinGenerator",
    "Inputs": ["Clock"],
    "Params": [1, 1.0, 1.0, 0.0, 0.0, 1]
  },
  "inverted": { "Type": "negate", "Inputs": ["signal"] }
}
```

Press Play on the Clock. The sine wave has frequency 1 Hz and amplitude 1;
`inverted` changes its sign. The clock's 200 Hz is the sampling rate, not the sine frequency.

## Common fields

| Field | Meaning |
|---|---|
| `Type` | Registered factory key, matched without case sensitivity. |
| `Inputs` | Names of upstream blocks. The receiver declares its inputs; order matters for Joiner and similar operations. |
| `Params` | Positional array defined by the block. Index 0 is the first parameter. |
| `DesiredRate` | Requested output rate or timing metadata; its effect depends on the block. |
| `Path` | Usually a recording directory; some blocks use it for model storage. |

`Params` is not a dictionary. A missing parameter may select a default, but wrong types
can throw or default depending on the implementation. Follow the
[block's parameter table](block-catalogue.md) and preserve positions, including hidden fields.
Editor defaults can differ from model defaults when metadata has drifted.

## Connections and data shapes

An edge carries an object, not a statically checked signal type. A vector is typically one
set of channel values; a matrix may contain a sample window or an image. Check **Consumes**
and **Publishes** before connecting blocks. Matching matrix types alone does not establish
matching row/column meanings.

Follow [Data shapes and time](data-and-time.md) for concrete examples and the rate at
each connection in [Signal Processing Walkthrough](signal-lab.md).

Both the canvas and JSON loader check declared input counts and allowed upstream block
classes. The loader also rejects duplicate edges, missing input names and feedback cycles.
These checks do not validate payload shapes. Use unique, descriptive names and update
`Inputs` references when renaming.

## Loading and failures

The loader validates registered block types and connections before constructing them.
Invalid definitions and their dependent definitions are skipped; unrelated valid blocks
can still load. Custom factories without type metadata are checked after construction.

If a constructor fails (for example, unavailable hardware), its already-created dependent
blocks remain visible for repair but disconnected. They do not run with only some of their
required inputs. Check the load report and log before starting; a partially visible graph
is not proof that everything loaded.

The graph builder retains input references to unavailable blocks. Keep the original
configuration when moving between platforms so you can recover settings for unavailable blocks.

## Saving and sharing

Use Save As for a new experiment and reopen it to verify parameters and connections.
Recording switches, histories and device state are not all persistent; see
[Recording and Saved Pipelines](recording.md).

Start from the [downloadable example](../examples/first-pipeline.json) when writing JSON.
Existing examples may contain comments accepted by MOSAIC; the example here is strict JSON.
