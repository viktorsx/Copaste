# Undo

A bounded stack (`kMaxUndo = 32`) of `UndoRecord`s, each one of two kinds.

## Transform records

`UndoKind.Transforms` — pushed before any operation that mutates existing
props: move drag (at the moment offsets are initialized, so an aborted drag
leaves no empty record), rotation bursts, height changes, nudge bursts, align
operations, Alt+wheel spin bursts, and delete.

The payload is a `TransformSnapshot` per selected prop:

| Field | Restores |
|---|---|
| `m_Entity`, `m_Prefab` | identity |
| `m_Transform` | position **and rotation** |
| `m_HadElevation`, `m_Elevation` | the `Game.Objects.Elevation` component (added/removed to match) |
| `m_HadTree`, `m_Tree` | tree growth state |
| `m_HasSeed`, `m_Seed` | `PseudoRandomSeed` (color variation) |
| `m_HasCustomColor`, `m_CustomColor` | Customization-tab colors |

Undoing a *move-like* record writes the snapshot back onto the still-existing
entities. Undoing a *delete* uses the same snapshots but has to rebuild the
entities — see below.

For burst-type inputs (nudge held, wheel spinning) one record covers the whole
burst: a record is pushed only on the first event, or when more than a second
passed since the previous one.

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

## What is *not* undoable

Blueprint file operations (save/delete/rename) and settings changes. Undo also
ends any live align session before applying (the session's geometry would be
stale after the world changed).
