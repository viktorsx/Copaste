# Fences, Networks & Curve Editing

Three partials implement everything behind the **Fences** and **Networks**
filter chips: `CopasteToolSystem.Fences.cs` (standalone fences),
`CopasteToolSystem.Networks.cs` (roads, paths, tracks) and
`CopasteToolSystem.Bending.cs` + `CopasteToolSystem.LaneAlign.cs` (curve
handles and lane alignment). User-facing behavior is in
[features.md](features.md); this page is the how.

## Fence anatomy

A placed fence is two invisible "container" nodes plus a container edge
(`Game.Net.Edge` + `Curve` + `Game.Tools.EditorContainer{m_Prefab =
NetLanePrefab}` + `PseudoRandomSeed`); the visible geometry is generated
lanes owned by that edge. Moving or bending a fence is a direct rewrite of
the curve and node positions plus `Updated` — the game regenerates the
lanes. Chained fences share a container node, so a moved link stretches its
neighbor's curve end to keep the joint closed.

## Network selection & transforms

Selection units are **nodes and edges**. A click prefers the node within
its own radius (min 6 m), otherwise the nearest edge by curve distance;
comparisons run in the xz plane with a small penalty per height difference
so bridges and tunnels stay pickable. Marquee takes nodes in the box and
edges whose sampled curve touches it.

Every gesture builds a "moving node set" (selected nodes + both nodes of
selected edges) and transforms it as one rigid piece:

- Nodes move with terrain-relative height preserved; **neighbor edges**
  (one node moving, the other anchored) move only the moving end, and
  their control points re-interpolate per-axis so curvature is preserved.
- **Endpoint offsets survive node moves**: a curve end legitimately does
  NOT have to sit on its node — the lateral end-to-node offset *is* lane
  alignment. Node transforms therefore re-derive each end as
  `end' = node' + rot*(end − node)` instead of snapping ends onto nodes;
  height-only operations shift the end's y alone.
- Pillars are node sub-objects and travel with their node; buildings,
  lanes and traffic reconnect through the game's own `Updated` machinery.

## Road copy → paste: the welding pipeline

Copying stores, per edge: the prefab, the **untouched** bezier curve
(centroid-relative, per-point heights above the source centroid terrain),
`Upgraded` flags, and indices into a **clipboard node table** — one entry
per source junction node (offset + height + node upgrade flags + node
prefab). Junction sub-object **markers** (roundabouts, manual traffic
lights, stop signs — prefabs whose `NetObjectData.m_CompositionFlags`
intersects `CompositionFlags.nodeMask`) are captured per node too.

Paste emits one `NetCourse` per edge through the game's definition
pipeline. The rules that make welding work:

1. **Curves are pasted bit-identical** (same shape, new anchor): the
   game's node-welding compares positions **bitwise**, so all courses that
   should share a junction must present the exact same point.
2. That shared point goes ONLY into `CoursePos.m_Position` of the course
   ends (from the node table) — the curve itself is never bent toward the
   node. Bending it was the historical "lanes re-centered" bug: ends
   often sit beside the node on purpose (lane alignment, trimmed
   roundabout approaches).
3. Courses carry `DisableMerge` on both ends — welding does not need the
   merge machinery, and letting courses into the game's overlap handling
   visibly reshaped parallel carriageways.
4. Node upgrade flags ride **zero-length courses** at the node point
   (start == end skips edge generation; the flags OR into the node), with
   terrain-relative elevation set — an elevation-less course can win the
   node merge and ground an elevated junction.
5. Markers re-attach after the paste resolves, via
   `CreationDefinition{m_Attached = node, Permanent|Attach}` +
   `ObjectDefinition` — the vanilla way junction markers are placed. The
   node is settled again a few frames later because markers are born
   after the roads.

Heights are **rigid**: one terrain reference at copy (source centroid) and
one at paste (anchor); bridge/tunnel elevation is recomputed against the
destination terrain, so the pasted piece keeps its shape instead of
draping over the new ground.

Undo/redo for all of this is described in [undo.md](undo.md#network-records).

## Curve handles (Bending.cs)

With exactly one fence or road segment selected, four handles appear: two
end handles on `a` and `d`, and two control-point handles at `b` and `c`
off the curve, each tied to its end by a thin line. Interaction is
**two-step**: the first click
only selects a handle (sticky, drawn green — PgUp/PgDn act on it), and a
drag starts only when the press lands on the already-selected handle, so
click jitter can never bend a road. Picks and drags both project the
cursor onto the horizontal plane at the handle's height — a raw terrain
hit lands tens of meters behind an elevated handle (parallax).

Moving an end keeps the shape: the control points are carried as
*offsets from the chord* at 1/3 and 2/3, rotated and scaled with the chord
(`RotateAndScale`), so manual arcs survive endpoint moves and a turn past
90° no longer mirrors the bend. Chord proportions were the earlier model
and were dropped: on a near-degenerate axis they exploded and flattened
hand-raised arcs. Mid-handle drags move the control point one-to-one, with
snap guides for straightening one half or the whole segment. The
*curve-body* grab (`kCurveHandleIndex`, within `kCurveGrabRadius` of the
axis) is what solves both control points so the curve passes through the
cursor: the required shift is split between `b` and `c` by their basis
weights at the grab parameter, and the grab is relative to the click point
so the curve never jumps to the cursor.

## Lane alignment (LaneAlign.cs)

With one road segment selected, a triangle sits beside each qualifying
joint (a node with exactly two real arms). Clicking cycles the joint
center → left → right:

- Targets come from the **composition's lane layout**
  (`NetCompositionLane`), not the live lane entities — the live sub-lane
  buffer briefly holds duplicate lane sets after every change and the
  measurements drifted. A live mid-edge measurement is kept only to
  orient the composition's axis sign, and as fallback.
- Roads **without pedestrian lanes** (highways, rural roads) use half the
  prefab-width difference instead — no sidewalks means total width IS the
  roadway, and edge-flush is exact there.
- The measuring/apply axis is anchored to the **wide** edge (alignment
  never moves it), so repeated clicks are idempotent; the narrow edge's
  travel direction only decides which side is "left".
- The shift moves the curve end **and its adjacent control point** by the
  same vector, keeping the joint tangent parallel to the through road —
  moving the end alone kinked the lane line right at the node.
- Equal lane layouts fall back to a quarter-roadway side-step preset.

## Straighten & slide

- **Tap Alt** puts every selected middle node (chains supported) onto the
  straight 3D line between its two anchors (first junction/dead end on
  each side), preserving spacing along the polyline, and flattens every
  chain edge — anchor edges included — into straight lines. Alt is also a
  modifier for other gestures, so the tap is edge-triggered: press arms,
  any click/wheel/other key disarms, clean release fires.
- **Alt during a node drag** slides the node along a line instead of
  following the cursor: a two-arm node projects onto the segment between
  its neighbors (clamped 1.5 m off each), an end node onto the extension
  of the line through the next two nodes — dragging the end of a crooked
  road straightens its last segment into the road's continuation.

## Underground mode

`UndergroundMode` (U key or the panel button) sets the tool's
`requireUnderground`, which flips the game into the underground view (the
bulldozer's mechanism). Picking and marquee then require
`MatchesUndergroundMode`: the candidate's position must be below terrain
(−1.5 m) exactly when the mode is on — for networks, props, buildings,
fences and surfaces alike. Copy/paste/undo are mode-agnostic.

## Gotchas

- Never bend a pasted course toward its junction: the shared point
  belongs in `CoursePos.m_Position` only. Bending the curve re-centers
  lane transitions (see the welding rules above).
- Zero-length node courses MUST carry elevation, or elevated junctions
  lose their `Elevation` on merge.
- The live sub-lane buffer of an edge is unreliable right after an edit:
  it can hold two full lane generations (neither `Deleted` nor `Temp`)
  with centimeter-level disagreement. Measure lane layout from
  `NetCompositionLane` instead.
- Marker attach definitions and recreated-road definitions need
  `KeepDefinitionsAlive` (a few frames of `ApplyMode.None`) or the
  tool's own per-frame `Clear` kills them before the game consumes them;
  every kept frame must also freeze paste preview/clicks.
- Deleting network pieces by geometry must compare **height** as well as
  xz — tunnels and bridges of the same prefab stack directly under/over
  surface roads.
