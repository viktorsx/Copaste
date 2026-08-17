# Buildings & Painted Surfaces

Added in 1.1.0, gated behind the panel's **Selection filters** (five hidden
settings: `SelectProps/Trees/Decals/Surfaces/Buildings`). With the Buildings
filter off the tool behaves exactly like the props-only versions.

## Selection

- `IsCopyable` admits entities with `Game.Buildings.Building` only while the
  Buildings filter is on (`IsCategoryEnabled`); `Extension` (building parts)
  stays excluded always.
- A separate `m_BuildingQuery` (Building + Transform + PrefabRef, no
  Extension/Owner/Temp/Deleted) feeds the marquee scan, Ctrl+click cycle
  picking and post-paste resolution — props scans stay building-free when the
  filter is off.
- **Painted surfaces** (`Game.Areas.Surface`, no `Owner`) are selected by the
  marquee when any polygon node falls inside the box, or, for surfaces bigger
  than the box, when the polygon centroid does. Selected surfaces are drawn as
  a polygon outline in the selection color and tracked in
  `m_SelectedSurfaces` — WYSIWYG: what is outlined is what copies.
- **Building-owned surfaces** (lot grass/decoration areas) join selection only
  behind the **Building elements** toggle + Surfaces chip, and only when the
  owner chain leads to a building (`GetOwnerRootBuilding` — road-owned areas
  never). They can be **selected and deleted (with undo)** but never
  transformed individually: `TransformSurface` refuses any area with `Owner`
  (the building sub-tree move is what moves them). Deleting one also deletes
  its `SubObject` children and thereby its decoration spawner — see the
  regeneration section below. Delete-undo restores the `Owner` link and the
  owner's `SubArea` buffer entry (same pattern as `RecreateProp`).

## What buildings support in this phase

Copy, paste, blueprints, undo of a paste, **transforms** (move drag,
right-drag group rotation, nudge, Alt+wheel spin, undo/redo), **Relocate**,
**road snap**, **height** (PgUp/PgDn, End, Match H — the whole lot moves
through the sub-tree primitive) and **delete with undo**. Align tools skip
buildings (the UI disables them when nothing in the selection can take
them). Buildings under construction (`Game.Objects.UnderConstruction`) are
never transformed — the construction flow would race our sub-tree writes —
but they CAN be deleted, and delete-undo snapshots them too.

## Selection filters

Five independent hidden settings (`SelectProps/Trees/Decals/Surfaces/
Buildings`) drive the panel's Selection card (bitmask binding
`selectionFilters`, triggers `toggleSelectionFilter` and
`soloSelectionFilter` — the latter is right-click solo). Category detection
is runtime-component based: `Building`; `Tree`/`Plant` = vegetation; an
object without `Game.Objects.Surface` (no collision surface) is a decal —
the same rule Move It uses; everything else is a prop. `IsCategoryEnabled`
gates `IsCopyable`, so click, marquee and Ctrl+click all honor the filters.

## Road snap and Relocate

`TryFindNearestRoad` queries the game's net search quad tree
(`Game.Net.SearchSystem.GetNetSearchTree`) in a 30 m radius around the
cursor (cached until the cursor moves 0.5 m; a cached edge that died — the
game splits/rejoins road edges around building changes — resets the cache).
`TryComputeRoadSnap` picks the closest point on the edge's bezier, offsets
by road half-width + lot half-depth to the cursor's side, and yields a yaw
facing the road (building local +z toward it).

- **Paste** (`ApplyRoadSnap`): the anchor building (clipboard item with the
  smallest offset that has `BuildingData`) snaps; the clipboard is rotated
  by the yaw delta so the whole group follows. Manual RMB rotation is
  ignored while snapped. Toggle: hidden `RoadSnapPaste` setting, panel
  switch visible only while the Buildings filter is on.
- **Relocate** (`Mode.Relocate`): the selected building follows the cursor
  with full updates every frame (single building — no throttle; a 250 ms
  tick was tried and felt awful), snapping when a road is near. Click
  settles and places; RMB/ESC restores the original transform and pops the
  undo record pushed on entry **by reference** (never blindly the top —
  another record may have landed meanwhile). Rotation is deliberately
  unavailable in this mode — snap owns the facing. Per-frame updates churn
  out orphaned sub-elements, so placement and cancel both run the orphan
  sweep (below) around the departure position.

## Deleting buildings

Delete adds vanilla `Deleted` (what the bulldozer does). Undo recreates the
building from the prefab archetype (`RecreateProp`) plus the
`UnderConstruction` kick so pavements and driveways regenerate, and a
delayed settle re-runs the road connection. Limitation, by design: the
restored building is a fresh instance — households and workers are gone,
because simulation state cannot be faithfully restored.

## Stuck "No car access" and the delayed settle

The settle after an operation raises `Updated` in the same frame as the
final writes, so `RoadConnectionSystem` can evaluate against search trees
that do not yet contain the moved sub-nets — the check misses and the
warning icon survives. `RunDelayedSettles` re-marks the building and its
`Building.m_RoadEdge` once more ~4 frames later, when the trees are fresh.

## Moving buildings (`CopasteToolSystem.Buildings.cs`)

The approach follows Move It (MIT, github.com/Quboid/CS2-MoveIt): transform
the building and its **world-space sub-tree by the same delta with direct
component writes**, mark everything `Updated`, and let the game regenerate
geometry, lanes and road connections. Only vanilla components are written, so
saves stay clean.

- `TransformBuilding(entity, delta, yawDelta, pivot)` is the single primitive:
  optional Y-rotation around a pivot followed by a translation. Move drag,
  nudge, group rotate, spin and the undo path all express through it.
- `CollectBuildingParts` walks `Game.Net.SubNet`, `Game.Net.SubLane`,
  `Game.Areas.SubArea`, recursing into `Game.Buildings.InstalledUpgrade` and
  `Pillar` sub-objects (depth ≤ 3, visited-set dedup). `ConnectionLane`
  children are skipped — the game re-links them to the public road.
- `TransformBuildingPart` writes by geometry type: `Game.Objects.Transform`
  for objects, `Game.Net.Node` position/rotation for net nodes (plus `Updated`
  on every `ConnectedEdge`, which is what re-snaps driveways to the road),
  all four `Game.Net.Curve` bezier points for edges and lanes, and the
  `Game.Areas.Node` polygon for sub-areas.
- Of the building's **direct** `SubObject` children only `Pillar`s are
  written: the rest are prefab-relative and the game repositions them when
  the building gets `Updated`. Sub-objects owned by the building's sub-areas
  and sub-nets (driveway/lot decals and decorations) have no prefab slot to
  come back to, so the walk recurses into them and they move rigidly with
  the tree.
- Undo/redo route through `MoveBuildingTo(entity, targetTransform)`, which
  derives the delta+yaw from the current state and finally writes the exact
  snapshot transform so no floating-point error accumulates across cycles.

**Painted surfaces** in the selection move with it: `SurfaceMoveItem` records
each polygon's centroid offset from the drag anchor, `TransformSurface`
shifts/rotates the `Game.Areas.Node` polygon (per-node terrain-height offset
preserved), and `SurfaceSnapshot` (a parallel list on `UndoRecord.m_Surfaces`)
gives surfaces the same undo/redo symmetry as objects. Surfaces are also
click-selectable (`TryPickSurfaceAt`, point-in-polygon with smallest-polygon
priority, gated on the Surfaces filter like the marquee), deletable
(`Deleted` + undo via `RecreateSurface` from the prefab's `AreaData`
archetype, `Complete` flag re-applied), and copyable on their own.

### Update throttling and sub-prop preservation

Marking the whole tree `Updated` every frame during a drag makes the game
fight the mouse: terrain leveling, road reconnection and prefab sub-object
re-layout all run 60×/s. So drags write positions + `BatchesUpdated` every
frame, but raise full `Updated` only on a 250 ms tick (`BuildingTick`) and
once cleanly at the end (`SettleBuilding` — which also `Updated`s the old
`Building.m_RoadEdge` so the stale road connection and its warning icon get
released, and refreshes all sub-objects for spawn-point reconnection).

The game re-lays a building's sub-objects from the PREFAB layout on every
`Updated` — wiping player customization. Countermeasures, both driven from
the relative layout captured at operation start (`CaptureSubPropLayout` in
`PushTransformUndo`, reset per operation via `ResetSubPropTracking`; the
walk covers the full tree including sub-net/sub-area-owned objects):
- `ScheduleSubPropRestore` + `RunSubPropFix` re-assert the captured relative
  transforms for ~10 frames after the operation (moved benches stay moved).
  A building that was never captured is refused (`m_SubPropCaptured` set) —
  restoring with an empty layout would classify the whole yard as
  "regenerated" and prune it.
- `PruneRegeneratedSubProps` deletes sub-objects that appear during that
  window but were not in the capture, or that match the deleted-sub-prop
  registry (below). Guard: `IsPrunableSubProp`, a filter-independent
  predicate (never buildings, extensions, vehicles, spawn points, markers,
  utility objects, placeholders) — deliberately NOT `IsCopyable`, which
  follows the live filter chips and would make pruning depend on UI state.

### Building elements toggle

`SelectBuildingProps` (UI label: **Building elements**) gates ALL access to
building-owned things: with it off, neither the marquee nor a plain click can
select them (`IsCopyable` checks `SelectBuildingProps || !IsOwnedByBuilding`).
With it on, owned props/trees/decals follow their filter chips, and owned
surfaces join behind the Surfaces chip. Road-owned elements are outside this
rule — their owner chain does not lead to a building.

### Deleted sub-props: the session registry

Direct building sub-props (trash cans, benches, yard trees) are selectable
via the **Building elements** toggle and deletable with undo. To keep them
deleted across later moves/relocates — the prefab re-layout would respawn
them — every delete records a signature `(prefab, local position)` in
`m_DeletedSubProps`, keyed by root building. Matching is within 1 m local
distance; capture skips matches, prune and `SweepBuildingSubElements`
re-delete them. Undo of the delete calls `ForgetDeletedSubProp`, so the
prop stays restored. **RAM only, never serialized** — the registry (capped
at 256 buildings) lasts one game session; saves stay vanilla-clean.

`SweepBuildingSubElements` deletes registry-signature matches ONLY. It had
distance/"factory" heuristics once; they escalated (1 → 58 → 110 deletions
per operation) fighting the game's legitimate rebuilds, and are gone.

### Regenerating deep-owned sub-elements (accepted limitation)

Decorations owned by the building's sub-areas/sub-nets (lot clotheslines,
driveway decals) are re-generated **with randomized placement** on every lot
update — including updates the simulation triggers by itself (tenant
turnover re-decorates the lot). A position signature cannot pin them down,
so `IsRegeneratingSubElement` (deep-ownership test: owner chain leads to a
building but the direct owner is neither the building nor an extension)
excludes them from selection and deletion entirely. Better Bulldozer
achieves real permanence here only by serializing per-prefab removal
records into the save file; Copaste deliberately does not write mod data
into saves, so per-instance deletion stays out of scope.

**The supported path instead: delete the spawner.** The decorations are
spawned by a decoration *surface* (sub-area) of the lot. With Building
elements + Surfaces on, that surface is selectable and deletable — plain
vanilla `Deleted` on the area (and its `SubObject` children), nothing in the
save, and the game does not rebuild sub-areas until the building itself is
rebuilt (level-up for growables, or delete+undo). Gone spawner, gone
clotheslines — permanently, across save/load. The same applies to any
other deep-owned decoration excluded from per-instance selection. (Yard
benches and chairs are NOT this type — they are direct sub-props,
selectable and deletable individually, kept deleted by the session
registry.)

### Lot transplant ("Paste look: Original" for lots)

Rebuilding a building through construction (paste, delete-undo, paste-redo)
produces a fresh RANDOMIZED factory lot — different path pieces, missing
front-walkway surface, re-rolled decorations. That clashes with the
"Original" paste-look promise, so every building capture — `ClipboardItem`,
`PastedRecord`, `TransformSnapshot` — records `SurfaceSig`s: prefab + the
FULL polygon of each lot surface in building-local frame (`null` = not a
building or still under construction; empty list = the player deleted them
all). After the rebuilt building finishes construction,
`SyncBuildingLotSurfaces` (scheduled via `m_PendingSurfacePrune`; the
countdown RESETS while `UnderConstruction` is present — a paused sim holds
construction indefinitely; the sync is strictly ONE-SHOT, repeating it
would duplicate surfaces) deletes ALL factory `Surface` sub-areas (with
their decoration children) and recreates the captured source surfaces as
building-owned areas at the new transform — same creation pattern as
`RecreateSurface`, with terrain-sampled node heights.

Gating: paste applies the transplant only with **Paste look: Original**
(`RandomPasteVariation` off) — "Random" keeps the construction roll.
Delete-undo and paste-redo always transplant (undo must be faithful).
Blueprints carry the capture as `BLOT`/`BSURF` lines (see
[blueprints.md](blueprints.md)). Pending syncs are never forced on tool
exit — syncing before the game has created every factory surface would
leave permanent duplicates. Entries survive deactivation and the countdown
resumes on the next activation. Copy also skips building-owned surfaces whose owner is in the
selection (anti-duplicate, same rule as owned props).

### Orphan sweep

The game's re-layout during moves can drop sub-elements out of ownership
buffers (its log says "Owner has no SubObject"), stranding them at the old
position. `SweepOrphansAround(position, radius)` deletes prunable objects
whose owner chain is dead (`HasDeadOwnerChain`) around the departure point
— run at drag release, relocate place/cancel and undo/redo teleports.

## The construction trick (why pasted buildings are complete)

A building's pavement sub-areas and driveway sub-nets are **not** created by
the object definition pipeline. They are built by
`Game.Simulation.BuildingConstructionSystem` (`CreateAreas` / `CreateNets`)
as construction finishes. A directly-created building skips construction and
therefore never receives them.

So after the post-paste resolution claims a pasted building, Copaste adds the
vanilla component:

```csharp
new Game.Objects.UnderConstruction { m_NewPrefab = prefab, m_Progress = 250, m_Speed = 200 }
```

Construction completes within a tick and the game itself builds the pavement,
driveways and attached props — identical to a hand-placed building. This is
done exactly once, at resolution time (never in the repeated fix-up pass, which
would loop construction forever).

Notes from testing: grown (zoned) buildings pasted outside zoning persist;
signature/unique buildings paste when *Anarchy while pasting* is on (the
placement error for duplicates is ignored). Households and workers are not
copied — the simulation moves people in, which is the desired behavior.

## Painted surfaces pipeline

Copy stores each selected surface as prefab + polygon offsets relative to the
selection centroid. Paste emits, through the same `ToolOutputBarrier`, a
definition entity per surface:

`CreationDefinition { m_Prefab = surfacePrefab }` + a `Game.Areas.Node` buffer
(polygon points at the paste anchor, terrain-sampled heights) + `Updated`.

`Game.Tools.GenerateAreasSystem` consumes exactly that shape (its definition
query is `CreationDefinition + Node + Updated`), previews the surface as a
Temp area and materializes it on apply. Group rotation rotates the polygon
offsets in the XZ plane together with the objects.

Post-paste, surfaces are resolved by prefab + polygon centroid (they have no
`Transform`), which feeds undo. On resolution the `AreaFlags.Complete` flag is
ensured — without it the game's own surface tool treats the polygon as
unfinished and refuses to edit it.

## Blueprint format addition

Surfaces serialize as their own line type (see
[blueprints.md](blueprints.md)): `AREA|prefabType|prefabName|x,z;x,z;...`.
Older mod versions skip these lines harmlessly.

## Gotchas

- Never re-add `UnderConstruction` in a repeated pass — one-shot at resolution
  only.
- Building sub-areas belong to the building (`Owner`); the surface machinery
  must only ever touch standalone painted surfaces or it will fight the
  construction system.
- Surfaces have no `Transform` — every generic "position of entity" code path
  must either skip them or use the polygon centroid.
- `AreaFlags.Complete` is load-bearing for editability.
- **Never delete freshly `Created` entities from a standing system in the
  same frame** — it corrupts the game's creation pipeline and crashes to
  desktop. If something regenerated must go, defer the deletion until the
  owner has settled (Better Bulldozer waits 30 quiet frames and goes through
  a command-buffer barrier).
- Sweeps must only act on **positive identification** (registry signature,
  dead owner chain). Distance/"looks wrong" heuristics escalate into a fight
  with the game's own rebuilds.
