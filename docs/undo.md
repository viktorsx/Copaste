# Undo & Redo

Two bounded stacks (`kMaxUndo = 32` each) of `UndoRecord`s, each record one
of three kinds: `Transforms`, `Delete`, `Paste`. Undoing moves a record (or
its inverse) onto the redo stack; redoing moves it back. Any NEW action
clears the redo stack (`PushUndo` → `m_RedoStack.Clear()`). The undo/redo/
delete hotkeys are ignored while a gesture is active (move drag, RMB
rotation, marquee) — firing mid-gesture would eat the gesture's own record.

## Transform records

`UndoKind.Transforms` — pushed before any operation that mutates existing
props: move drag (at the moment offsets are initialized, so an aborted drag
leaves no empty record), rotation bursts, height changes, nudge bursts, align
operations, and Alt+wheel spin bursts.

The payload is a `TransformSnapshot` per selected prop:

| Field | Restores |
|---|---|
| `m_Entity`, `m_Prefab` | identity |
| `m_Transform` | position **and rotation** |
| `m_HadElevation`, `m_Elevation` | the `Game.Objects.Elevation` component (added/removed to match) |
| `m_HadTree`, `m_Tree` | tree growth state |
| `m_HasSeed`, `m_Seed` | `PseudoRandomSeed` (color variation) |
| `m_HasCustomColor`, `m_CustomColor` | Customization-tab colors |

Undoing a Transforms record writes the snapshot back onto the still-existing
entities; the pre-undo state is captured into a fresh inverse record for the
redo stack. Building snapshots route through `MoveBuildingTo` (sub-tree
move + delayed settle + orphan sweep around the departure position);
surfaces restore through the parallel `m_Surfaces` snapshot list.

For burst-type inputs (nudge held, wheel spinning) one record covers the whole
burst: a record is pushed only on the first event, or when more than a second
passed since the previous one.

## Delete records

`UndoKind.Delete` — pushed by Delete with a full `TransformSnapshot` list
(props, buildings — including under-construction ones — and surfaces).
Undo rebuilds every entity (below), redo deletes them again. Order matters:
the record is placed on the redo stack **before** recreation, so
`RemapHistoryEntity` can rewrite the old entity ids inside it (and in every
other record on both stacks) as each entity comes back — without the remap,
a second undo/redo cycle would reference dead entities.

Recreating a deleted building adds the `UnderConstruction` kick (pavement +
driveways regenerate) and schedules a delayed settle; recreating a building
sub-prop also calls `ForgetDeletedSubProp` so the session registry stops
re-deleting it (see
[buildings-and-surfaces.md](buildings-and-surfaces.md#deleted-sub-props-the-session-registry)).

## Recreating deleted props

`RecreateProp(snapshot)` follows the LineToolLite approach: create an entity
directly from the prefab's `ObjectData.m_Archetype`, then set `PrefabRef`,
transform, elevation, tree state, the snapshot's `PseudoRandomSeed` (so a
deleted blue bench comes back blue; a random seed is used only if the snapshot
has none), and custom colors via `ApplyInstanceColors`.

Two placement quirks:

- `EntityManager.CreateEntity(archetype)` (the 1-arg overload) doesn't compile
  under the net48 toolchain (span types); the 3-arg
  `CreateEntity(archetype, 1, Allocator.Temp)` variant is used instead.
- Freshly created entities can stay invisible (batches never built) or get
  instantly hidden as overlapping. `BatchesUpdated` fixes the former; adding
  Anarchy's `PreventOverride` (when available) prevents the latter.

## Paste records

`UndoKind.Paste` — pushed at stamp time. The payload is the same
`PastedRecord` list the post-paste fix resolves (see
[clipboard-and-paste.md](clipboard-and-paste.md#post-paste-fix-up)); by the
time the user hits Ctrl+Z the records carry the actual entity ids the paste
created. Undo deletes exactly those entities — identical pre-existing props on
the same spot are untouched, because resolution claims at most one entity per
record.

Undo works inside paste mode too (it removes the last stamp and marks the
preview dirty so it rebuilds).

Redo of an undone paste recreates the pasted entities from the snapshots the
undo took just before deleting them (`SnapshotResolvedPasted` /
`SnapshotResolvedPastedAreas`), with the same remap discipline as delete
records. Stamp-time records also carry a pre-stamp exclusion set of
identical existing buildings in the paste bounds, so resolution can never
"adopt" a pre-existing twin and undo can never delete it.

## What is *not* undoable

Blueprint file operations (save/delete/rename) and settings changes. Undo also
ends any live align session before applying (the session's geometry would be
stale after the world changed).
