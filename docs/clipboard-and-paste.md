# Clipboard & Paste

## The clipboard model

`CopySelection()` converts the selection into a list of `ClipboardItem`s:

| Field | Meaning |
|---|---|
| `m_Prefab` | prefab entity |
| `m_Offset` | position relative to the selection centroid |
| `m_Rotation` | world rotation |
| `m_HeightOffset` | height above terrain at the source location |
| `m_Diameter` | cached footprint diameter (for overlay circles) |
| `m_HadTree`, `m_Tree` | tree growth state, so pasted trees keep their age |
| `m_HasSeed`, `m_Seed` | the original's `PseudoRandomSeed` (color/mesh variation) |
| `m_HasCustomColor`, `m_CustomColor` | custom `ColorSet` if the prop was recolored via the vanilla Customization tab |
| `m_PreviewSeed` | a random-but-fixed seed for stable preview colors (see below) |

The clipboard is replaced wholesale by every copy (and by loading a blueprint).
There is deliberately no "clear clipboard" action — nothing happens until the
user pastes.

## Entering paste mode

`EnterPasteMode` switches `m_Mode`, remembers the game's previous
`ignoreErrors` flag and, if the *Anarchy while pasting* option is on, sets
`ToolSystem.ignoreErrors = true` so placement validation doesn't reject
overlaps. The flag is restored on exit. ESC or a quick right-click leaves paste
mode.

## The preview loop

Each `UpdatePasteMode` frame raycasts terrain+roads for the anchor point. Only
when the anchor moved (or `m_PasteDirty` is set — e.g. a different blueprint was
loaded, or undo ran) does `CreatePasteDefinitions(anchor)` rebuild the
definition entities; otherwise `applyMode = None` keeps the existing preview.

`CreatePasteDefinitions` also records a `PastedRecord` per item in
`m_LastPreview` — prefab, final position, tree state, seed, custom color — used
both for the post-paste fix-up and for preview color correction.

If the anchor lands on a road/path surface (hit above terrain), the whole group
is lifted by that delta (`GetAnchorHeightDelta`), so pasting onto a bridge or
plaza keeps props on the surface.

### Stamping

A click sets `applyMode = ApplyMode.Apply` — the game makes last frame's Temp
preview permanent — and hands `m_LastPreview` to the post-paste fix. The
clipboard stays loaded, so repeated clicks stamp repeatedly. Ctrl+Z inside paste
mode removes the last stamp.

## Color & variation preservation ("Original" mode)

CS2 props get their look from two sources:

1. **`PseudoRandomSeed`** (vanilla component, ushort) — indexes the prefab's
   built-in color/mesh variations. Assigned by the game from
   `CreationDefinition.m_RandomSeed` at placement.
2. **`Game.Rendering.CustomMeshColor`** (vanilla, `IBufferElementData`,
   enableable) — per-instance custom colors from the game's Customization tab.
   When present *and enabled*, its `ColorSet` overrides the variation. The
   current rendered colors live in the `Game.Rendering.MeshColor` buffer.

With the panel toggle on **Original** (default; persisted in settings as
`RandomPasteVariation == false`):

- Copy captures the source's seed and, if the `CustomMeshColor` buffer exists
  **and is enabled**, its first `ColorSet` (`TryGetCustomColor`).
- Paste definitions use the fixed `m_PreviewSeed` per item **only when the item
  carries an original seed** — the fixed seed keeps the ghost from re-rolling
  colors every frame, and the real seed overwrites it after apply. Items with
  no captured seed (legacy blueprints, props without the component) get a fresh
  random seed per stamp, otherwise every stamp would be an identical clone.
- After apply, `ApplyPastedFix` (below) writes the original `PseudoRandomSeed`
  and, for custom-colored items, `ApplyInstanceColors`.

With **Random**, records carry no seed/color and the game's own randomization
applies per stamp.

### ApplyInstanceColors

Writes a `ColorSet` onto an entity the way the vanilla Customization tab does:
fill the `CustomMeshColor` buffer (one element per `MeshColor` submesh),
`SetComponentEnabled<CustomMeshColor>(true)`, mirror the colors into the
`MeshColor` buffer for immediate display, add `BatchesUpdated`. No-ops when the
prefab has no `CustomMeshColor` buffer in its archetype (prop doesn't support
customization) and when the target colors are already applied (the post-paste
fix runs for several frames).

## Post-paste fix-up

The game creates the final entities, so Copaste must find them again.
`RunPostPasteFix()` runs for up to 10 frames after a stamp:

- For records already resolved, it just re-applies the fixes (idempotent).
- Unresolved records are matched against `m_PropQuery` within a bounding box:
  same prefab, position within 10 cm, **at most one entity claimed per record**
  (so pre-existing identical props on the same spot are never captured — that
  matters for undo).
- Per matched entity, `ApplyPastedFix` applies: Anarchy protection
  (`Overridden` removal + `PreventOverride` via reflection), tree age, original
  seed, custom color.
- The resolved list is *also* the undo record's payload — undo deletes exactly
  the entities the paste created.

## Preview color correction

The ghost Temp entities are created by the game and would show colors derived
from the definition seed, not the original's. `UpdatePreviewLook()` runs for
10 frames after each definition rebuild (Temp entities lag definitions by a
frame) and, for each of **our** Temp entities near the preview, sets the
original `PseudoRandomSeed` / custom colors so the ghost shows what will really
be stamped.

Two hard rules learned the hard way:

- **Skip any Temp with `m_Original != Entity.Null`.** The game also creates
  Temp proxies for *existing* objects affected by the placement; recoloring one
  of those would permanently restyle a bystander prop when the user clicks.
- Matching ghost→record is nearest-neighbor per prefab; it is only used for the
  *preview*. The authoritative transfer happens in the post-paste fix, which
  matches by exact position.

## Duplicate in place

Pressing Ctrl+V with a selection and clicking without moving pastes at the
same spot; undo of that paste removes only the new copies (thanks to the
one-entity-per-record rule above), never the originals.

## Gotchas

- `CreationDefinition.m_RandomSeed` → final `PseudoRandomSeed` mapping is the
  game's business; never try to invert it. Overwrite the seed after apply
  instead.
- `CustomMeshColor` is an **enableable** component — checking `HasBuffer` is
  not enough, disabled buffers must be treated as "no custom color".
- Everything written here (seed, tree state, custom colors, transforms) is a
  vanilla, save-serialized component. Copaste adds no custom data to saves.
