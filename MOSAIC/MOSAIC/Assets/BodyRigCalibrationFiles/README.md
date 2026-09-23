# BodyRig kinematic models

The calibration profiles the BodyRig card opens on by default: the `double_hand` and `bimanual`
models from the AIROB lab practical (`IMU_BodyRigDay`), preserved byte-for-byte from the originals
(dated 2023-03-07).

## How this folder is used

Unlike everything else under `Assets`, these are **not** embedded as Avalonia resources. They are
copied beside the executable into `BodyRigCalibrationFiles/`, because the card lists them with
`Directory.GetFiles` and Store writes new profiles back — neither works against a resource
compiled into the assembly.

A BodyRig card whose pipeline names no profile folder falls back to that deployed copy, so a fresh
checkout opens on these models with nothing to configure. A folder named in the pipeline always
wins; the fallback only fills a blank. It is resolved once at startup
(`BodyRigViewModel.DefaultProfileDirectory`) and skipped if the folder was not deployed.

Two consequences worth knowing:

- Profiles you Store from the card land in the **output** folder, so `dotnet clean` or a fresh
  clone loses them. Copy anything you want to keep back into this source folder.
- Every file here is copied and therefore listed as a profile — except `*.md`, which the project
  file excludes so this README does not show up in the picker.

## File format

One record per segment, colon-separated, invariant culture:

```
linkX : linkY : linkZ : dhW : dhX : dhY : dhZ : parent : sensor [ : name ]
```

`parent` is a segment index or `-1` for a root. `sensor` is the **positional slot** in the joined
quaternion vector, not the peripheral number set on the ESP node — the card shows both, as
`S2 · #70`. The tenth `name` field is a MOSAIC addition; both files here stop at nine, which loads
fine and leaves every segment unnamed.

## What the models describe

`double_hand` — a torso root with two symmetric 3-segment arms, matching the seven placements in
the protocol (chest, both upper arms, both forearms, both hands):

```
3  chest        root, link 0.38 × 0 × 0.38
├─ 2  (0.30) ─ 1  (0.30) ─ 0  (0.00)
└─ 4  (0.30) ─ 5  (0.30) ─ 6  (0.00)
```

`bimanual` — the same torso root with three branches, and shorter links:

```
3  root         link 0
├─ 2  (0.28) ─ 1  (0.18) ─ 0  (0.00)
├─ 4  (0.00)
└─ 5  (0.19) ─ 6  (0.01)
```

Segments 4 and 5 are both bound to sensor slot 4, and slot 5 is unused. Two segments driven by one
IMU is legal and deliberate.

**Probe count:** `double_hand` binds slots 0–6 and needs **seven** probes; `bimanual` needs seven
groups too, since it binds slot 6 even though slot 5 goes unused. Seven probes are on hand
(13, 18, 70, 72, 73, 74, 92 — see `Examples/BlockTestFiles/BodyRig7Sensors.json`), so both models
run as shipped, unedited.

On a six-probe rig `double_hand` is simply not possible, but `bimanual` fits if segment 6's slot
is changed 6→5 — do that on a copy, so this file keeps matching what the lab protocol describes.

## History: these files are why the chain became a tree

Both models branch, and `BodySegment` used to hold a **single** `Child`. `BodyRig.SetParent`
detached a parent's previous child when a second one was attached, so branches were silently
severed on load while `LoadCalibration` reported success:

| Model | File says | What the old single-child model produced |
|---|---|---|
| `double_hand` | segments 2 and 4 both parented to 3 | segment 2 → parent `-1`; the 2‑1‑0 arm became a **detached root** |
| `bimanual` | segments 2, 4 and 5 all parented to 3 | segments 2 and 4 → parent `-1`; only segment 5 kept its parent |

A detached root takes the `Parent is null` branch of `BodySegment.UpdateOrientation`, so its
position is computed from the world origin rather than from the torso — the limb rendered hanging
off the origin instead of off the body.

`BodySegment` now holds `Children` and propagates forward kinematics down every branch, so both
models load with their full topology. `BodyRigBranchingTests` pins that against copies of these
exact records; if you edit the files here, update those copies too.
