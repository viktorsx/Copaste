# Blueprints

Blueprints persist a clipboard to disk so prop groups can be reused across
cities and saves. They are deliberately **plain text files outside the save
format**:

```
%LocalLow%\Colossal Order\Cities Skylines II\ModsData\Copaste\Blueprints\<name>.txt
```

## File format

Line 1 is the magic header `COPASTE1`. Every following line is one prop,
`|`-separated. Fields by index:

| # | Field | Notes |
|---|---|---|
| 0 | prefab type name | e.g. `StaticObjectPrefab` — used with field 1 as a `PrefabID` |
| 1 | prefab name | |
| 2–4 | offset x/y/z | relative to the group centroid, `R` round-trip floats, invariant culture |
| 5–8 | rotation quaternion x/y/z/w | |
| 9 | height offset | height above terrain at copy time |
| 10 | diameter | footprint cache for overlays |
| 11 | had tree flag (`1`/`0`) | since v1.0.4 |
| 12 | tree state (int) | |
| 13 | tree growth (byte) | |
| 14 | pseudo-random seed | since v1.0.6; `-1` = none |
| 15 | custom color | since v1.0.6; `-` = none, else `r,g,b,a;r,g,b,a;r,g,b,a` (three ColorSet channels) |
| 16 | prefab hash | since v1.2.0, written only for Paradox Mods assets (vanilla lines stay 16 fields) |

**Backward compatibility:** the loader accepts line lengths 11 (v1.0.0), 14
(v1.0.4), 15 and 16 (v1.0.6), and 17 (v1.2.0). Fields that a shorter format
lacks simply stay at their defaults. The writer always emits the newest
format. Names containing `|` are skipped at save time.

**Asset hashes (v1.2.0):** the game identifies a prefab by type + name +
asset hash, and Paradox Mods assets register with a non-empty hash — a
name-only lookup misses them. Lines referencing such assets carry the hash
(field 16 for props; an extra field before the polygon for `AREA`/`BSURF`);
loading tries the hashed lookup first and falls back to name-only, so old
files still resolve and a hash mismatch is logged. Field 16 is reserved:
any future prop field must come after it. Hashed `BSURF` lines are written
at the end of their lot block so the 1.1.0 loader skips only them, not the
rest of the block.

**Painted surfaces (v1.1.0):** serialized as their own line type,
`AREA|prefabType|prefabName|x,z;x,z;...` — the polygon as centroid-relative
XZ pairs (`;`-separated, invariant culture). Loaders older than 1.1.0 skip
these lines because the field count doesn't match any object format.

**Building lot surfaces (v1.1.0):** a building line may be followed by
`BLOT|n` and then `n` lines of `BSURF|prefabType|prefabName|x,z;x,z;...` —
one per lot surface of the source building, the polygon in building-local
XZ. They apply to the most recently parsed item. With *Paste look:
Original*, after paste construction the factory lot surfaces are replaced
by exact copies of these, so the copy's lot looks like the source's
(deleted surfaces stay deleted, reshapes carry over). `BLOT|0` with no
BSURF lines means the source had none left — the copy's factory surfaces
are all removed. Old loaders skip both line types (field counts and type
tags match no known format).

**Fences (v1.2.0):** their own line type,
`LANE|prefabType|prefabName|hash|seed|x,z,h;x,z,h;x,z,h;x,z,h` — the four
bezier control points as centroid-relative XZ plus each point's height
above terrain (`-` = no hash, `-1` = no seed). Pasting rebuilds the curve
on the destination terrain. Old loaders skip the line.

**Roads (v1.2.0):** two line types. `NETNODE|x,z,h|g,l,r` — one per source
junction node, in order: centroid-relative XZ + height above terrain, then
the node's upgrade flags (three uints, or `-`). The node table is what lets
a pasted blueprint weld its segments back together (welding needs a
bit-identical shared point).
`ROAD|prefabType|prefabName|hash|upgrades|x,z,h;×4|start,end` — the
four-point curve encoding as fences; `upgrades` is three uints `g,l,r`
(composition flags general/left/right) or `-`; `start,end` are indices
into the NETNODE table (`-1` = unknown, falls back to proximity welding).
`NETMARK|index|prefabType|prefabName|hash` — one line per junction marker
(roundabout, manual traffic light, stop sign), pointing at its NETNODE by
index. These are sub-objects of the node rather than upgrade flags, so they
cannot ride in the NETNODE line, and a node can carry several.
Malformed NETNODE lines keep their slot as a placeholder so later indices
stay valid, and a NETMARK pointing at such a slot is dropped on load. Old
loaders skip all three line types.

## Saving

`SaveBlueprint()` snapshots **the current selection** (falling back to the
clipboard only when nothing is selected — e.g. re-saving a loaded blueprint).
The panel only shows the Save button while something is selected, which
prevents the classic "saved the previous clipboard by accident" mistake.

## Loading

`LoadBlueprint(name)` parses the file into a fresh clipboard. Prefabs are
resolved via `PrefabSystem.TryGetPrefab(new PrefabID(type, name))`; entries
whose prefab doesn't exist in the current game (missing DLC/asset) are counted
and skipped, and the load succeeds with the rest. Each loaded item gets a new
`m_PreviewSeed`. The UI triggers a load and immediately enters paste mode (or
refreshes the preview if already pasting).

## Name sanitization

All names coming from the UI pass through `SanitizeBlueprintName`:
`Path.GetInvalidFileNameChars` are stripped, `..` is rejected, and the result
must be non-empty. This runs on load, save, delete and both ends of rename —
the panel is cohtml content, so filenames must be treated as untrusted input
(path traversal).

## Rename

Rename is a UI-side inline input; the trigger payload is
`oldName + "\n" + newName`. While typing, the UI raises the `setTyping` flag so
tool hotkeys (T, Home, Delete…) don't fire mid-word.

## Panel list

The panel shows blueprints five per page with a small pager in the section
header (cohtml doesn't render styled scrollbars, so paging beats an invisible
scroll area). Clicking a name loads and starts pasting; each row also has
rename and delete icon buttons.
