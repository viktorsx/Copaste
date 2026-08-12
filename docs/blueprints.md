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

**Backward compatibility:** the loader accepts line lengths 11 (v1.0.0), 14
(v1.0.4), 15 and 16 (v1.0.6). Fields that a shorter format lacks simply stay at
their defaults. The writer always emits the newest format. Names containing
`|` are skipped at save time.

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
