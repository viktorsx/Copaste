# Tool System — Select Mode

Everything in this document lives in `src/CopasteToolSystem.cs` and runs from
`UpdateSelectMode()`.

## Raycasting

The tool relies on the game's `ToolRaycastSystem`, configured per frame in
`InitializeRaycast()`:

- **Select mode:** `TypeMask.StaticObjects | Terrain` — the ray can hit props
  and the ground (the ground is needed so a marquee can start on empty terrain).
- **Paste mode, move drag, and while the marquee is held:**
  `TypeMask.Terrain | Net` with road/pathway layers — the ray deliberately
  ignores objects so the dragged/pasted group doesn't "climb" onto props and
  buildings the cursor crosses, but can still land on road surfaces.

`GetRaycastResult(out entity, out hit)` returns **one** hit — the nearest
surface. The hit entity is filtered through `IsCopyable` (see below); anything
that fails it yields `hitEntity == Entity.Null` even though the raw
`raycastEntity` may be something else (a vehicle, a lot decoration…).

### IsCopyable

An entity can be selected iff it has `Object` + `Transform` + `PrefabRef` and
passes every gate: not an invisible spawn point (`SpawnLocation` with an empty
prefab `SubMesh` buffer — benches/chairs carry `SpawnLocation` too and stay
selectable), no `Marker`/`UtilityObject`/`Placeholder`, not a regenerating
deep-owned sub-element, its category chip is on (`IsCategoryEnabled` — this is
also how buildings are admitted behind the Buildings chip), no
`Extension`/`Vehicle`/`Moving`/`Creature`, building-owned only while
**Building elements** is on, and no `Temp`/`Deleted`. Marquee scans and cycle
picking draw from three queries — `m_PropQuery` (free-standing),
`m_OwnedPropQuery` (behind Building elements) and `m_BuildingQuery` (behind
the Buildings chip) — with the per-entity gates applied in the scan; see
[buildings-and-surfaces.md](buildings-and-surfaces.md).

## The click chain

`ClickedThisFrame()` press handling is one prioritized `if/else if` chain. Order
is load-bearing:

1. **"To prop" pick armed** → clicked prop becomes the align reference.
2. **Match H pick armed** → clicked prop donates its height.
3. **Ctrl+click cycle picking** (see below) — only when the ray actually hit an
   *object* (prop or building) and a candidate exists; otherwise the click falls
   through.
4. **Press on a prop** → remember it (`m_LeftPressEntity`); on release it's a
   click-select, or if the mouse traveled > 0.4 m first it becomes a move drag.
5. **Press on ground** → marquee start (never on buildings, so the box corner
   can't anchor on a roof).

Pick modes are mutually exclusive (arming one disarms the other) and auto-disarm
when the selection becomes empty, so a swallowed-click state can't persist.

## Click selection

`ApplyClickSelection(entity, shift)`:
- plain click: selection = {entity}; clicking the already-single-selected prop
  keeps it
- Shift+click: toggles membership
- while the type filter is active, clicking a prop *switches the filter* to that
  prop's type instead
- marks the selection as non-marquee (`m_SelectionFromMarquee = false`), which
  is what lets the panel show a single prop's name

## Ctrl+click cycle picking

Problem: the ray always hits the topmost surface, so a prop partially buried in
a bigger object (or in a building) can never be click-selected.

`CyclePick(point, topHit)`:
1. Scan `m_PropQuery` for copyable props whose **3D** distance to the hit point
   is within `max(2.5 m, diameter/2 + 0.5 m)` (a cheap 30 m rejection runs
   before the per-entity diameter lookup — the query spans the whole map).
2. Sort candidates by distance.
3. If the click is within 1 m of the previous Ctrl+click, advance an index and
   return the next candidate (wrapping); otherwise return the nearest.

Repeated Ctrl+clicks on the same spot therefore cycle through everything piled
up there. Shift+Ctrl+click adds the pick to the selection. A Ctrl+click on bare
terrain finds no candidates and falls through to normal handling — important
because users hold Ctrl for nudging and still expect click-to-deselect and
marquee to work.

## Marquee selection

The box is **camera-aligned**: at drag start the camera's forward is projected
onto the ground plane and stored as `m_MarqueeForward`/`m_MarqueeRight`, and
membership tests are dot products in that basis. The box activates after 1 m of
drag (so a sloppy click doesn't open a 5 cm box) and re-scans candidates only
when the corner has moved > 0.25 m — scanning all objects each frame is the
expensive part.

Membership uses each prop's **footprint radius** (from the prefab's geometry
data, cached per prefab), not just its center, so props visually inside the box
don't get skipped when only their center is outside. Selection is capped at
`kMaxSelection = 1000`.

Marquee-built selections are marked `m_SelectionFromMarquee = true`; the panel
uses this to suppress the single-prop name row.

## Move drag

Press on a prop and travel > 0.4 m → the whole selection follows the mouse
(the pressed prop joins the selection if it wasn't in it).

Two subtleties:

- **Parallax fix.** At press time the ray hits the prop's surface; from the next
  frame the drag mask hits terrain only. Those two hit points differ by the
  prop's height projected along the view ray, which used to cause a visible jerk
  at drag start. Therefore `BeginMoveDrag` only flags the drag as pending and
  the per-prop offsets are computed in `InitMoveOffsets` from the **first
  terrain hit** — the same kind of anchor every later frame uses.
- **Undo is pushed in `InitMoveOffsets`,** not at drag start, so a drag that
  aborts before actually moving anything doesn't leave a no-op undo record.

**Alt+drag** moves only the grabbed prop (if it belongs to the selection)
instead of the whole selection — used for touch-ups after align operations.

Height above terrain is preserved per prop: each `MoveItem` stores
`heightOffset = y - terrainHeight(pos)` and the new y is re-sampled at the
destination.

## Rotation

- **RMB drag** rotates the whole selection around its center; holding **Alt**
  snaps to 45° steps (`kRotateSnap`).
- **Alt + mouse wheel** spins **each prop around its own axis**, 15° per wheel
  notch (`SpinSelection`). One undo record covers a whole burst (records are
  only pushed when > 1 s passed since the last wheel event). Note the game's
  camera zoom also reacts to the wheel; the tool cannot consume raw input.
- A quick right-click (no drag) steps back: disarm pick modes → clear
  selection → deactivate tool.

## Height controls

- PgUp/PgDn: raise/lower the selection (works in paste mode too, adjusting the
  preview's height boost)
- End: snap to ground (reset elevation)
- Home / "Match H": arms height picking; the next clicked prop's absolute
  height is applied to the whole selection
- Elevation is kept consistent: whenever a prop is moved its
  `Game.Objects.Elevation` component is updated via `WriteElevation` so the
  game doesn't later "correct" the height.

## Nudge

Ctrl+Arrow keys move the selection continuously at 1 m/s, camera-relative
(the same basis logic as the marquee). The first press in a burst pushes one
undo record.

## Type filter ("Select same", T)

`ToggleSameFilter()` takes the prefab of the hovered prop (or the first
selected one), narrows the current selection to that prefab, and stores it as
`m_SameFilterPrefab`. While active, marquee scans skip every other prefab and
clicking any prop re-targets the filter. Press T again to clear. The filter's
display name is resolved once per change (not per frame).

## The "Selected props" panel list

For small selections (2–50, `kSelectionListMax`) `GetSelectionList()` emits
`entityIndex:entityVersion:prefabName` lines for the panel. Hovering a row sets
`m_ListFocusEntity`, which `DrawSelectOverlays` rings in green so identical
props can be told apart; clicking a row calls `SelectOnly(index, version)` which
reduces the selection to that prop. Prefab names are cached per prefab entity
in `m_PrefabNameCache`. When the list is not shown the focus entity is cleared,
so a stale ring can't survive the list unmounting.

## Overlays

`DrawSelectOverlays` draws circles via `OverlayRenderSystem`: blue for selected
props (capped at 400 circles), white for the hovered prop, green for the
panel-focused prop, plus the marquee rectangle. Circle diameters come from the
same cached footprint data as the marquee test.

## Gotchas

- The raycast returns a single hit. Anything "under" another surface needs the
  cycle-pick path — do not try to special-case the raycast masks per prop.
- Branch order in the click chain matters; new gestures must slot in *below*
  the armed pick modes and must not swallow empty-ground clicks (Ctrl is held
  for nudging).
- Never compute drag offsets from a mixed-mask hit (prop surface vs terrain) —
  that's the parallax jerk.
- Raw `Keyboard.current` / `Mouse.current` reads happen even when the cursor is
  over the panel; state-changing reactions to raw clicks must be guarded by
  raycast validity (panel clicks produce an invalid tool raycast).
