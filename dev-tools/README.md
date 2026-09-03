# Development instrumentation (private repo only)

Two tools that were built during 1.2.0 development and taken out before
release. They are kept here so the next investigation does not start from
scratch. **Neither is part of the build**: the `.cs.txt` extension keeps them
out of the project's source glob.

Nothing in here is ever published. This folder lives on the private
repository only.

## What they are

**`CopasteProfiler.cs.txt`** measures what the mod costs per frame. It
timestamps both `OnUpdate` bodies (the tool system and the UI system),
buckets the samples by what the user is doing, and every ~600 frames appends
a block to `ModsData/Copaste/performance.txt` with averages, worst cases and
the cost per second of gameplay at 60 fps. It also times sections inside the
tool update (raycast, network hover, overlay drawing, deferred queues,
marquee scan) and counts two allocation-heavy paths. Sampling is two
timestamp reads per system per frame into preallocated arrays, so it does not
distort what it measures.

**`CopasteToolSystem.Diagnostics.cs.txt`** writes a full picture of a road
copy/paste into `ModsData/Copaste/diagnostics.txt`: the source graph, the
clipboard node table, the pasted graph, per-junction gap columns, axis
tangent comparisons and node flag comparisons. This is what finally explained
why pasted interchanges came out as dead ends, after three wrong guesses.

## Restoring them

Everything, files and call sites both, is at the tag **`dev-tools-1.2.0`**
(on the private remote). The commit right after that tag is the removal, so
its diff is the exact recipe in reverse.

    git show dev-tools-1.2.0 --stat
    git checkout dev-tools-1.2.0 -- src/CopasteProfiler.cs src/CopasteToolSystem.Diagnostics.cs

Then put the call sites back. To see exactly which lines were taken out:

    git log --oneline --all --grep="Phase 5"      # find the removal commit
    git show <that commit>                        # its diff, reversed, is the recipe

Or restore the whole set of touched files at once, then re-apply any later
work on them by hand:

    git checkout dev-tools-1.2.0 -- src/CopasteToolSystem.cs src/CopasteToolSystem.Networks.cs src/CopasteToolSystem.LaneAlign.cs src/CopasteUISystem.cs

### Where the call sites were

- `CopasteToolSystem.OnUpdate` — `CopasteProfiler.Begin()` at the top, the
  matching `End(SlotTool, …, ProfileState)` in a `finally`, plus the
  `ProfileState` property that classifies the current activity.
- `CopasteUISystem.OnUpdate` — `Begin()`/`End(SlotUi, …)` around the binding
  updates, and `Tick(state)` at the end. The UI system runs every frame even
  when the tool is off, which is why the per-frame tick lives there.
- Sections — around `GetRaycastResult`, `UpdateNetHover`,
  `DrawSelectOverlays`, the `RunPending*` block and `UpdateMarqueeHits`.
- Counters — `CountSelectionListBuild()` inside the selection-list builder,
  `CountMarqueeScan()` next to the marquee scan.
- Diagnostics — `DiagCaptureSource` at the end of `CaptureNetworkEdges`,
  `DiagWriteReport` inside `LogPastedNetTopology`, and in `LaneAlign.cs` the
  `DiagLaneAlignClick`/`DiagDumpEdgeLanes` pair with the `m_Diag*` fields on
  `LaneAlignSpot`.

## Baseline measured on 2026-08-28

For comparison if this is ever re-run. Microseconds per frame; the budget for
one frame at 60 fps is 16667.

| State | Before optimisation | After |
|---|---|---|
| Tool off | 3.6 (tool system 0.00, it does not run) | 3.2 |
| Tool open, nothing selected | ~200 (raycast 186 of it) | 94 (raycast 78) |
| 1 to 100 selected | 2371 | ~2100 |
| Over 100 selected | 6568, peaks of 21424 | ~3000, peaks ~4500 |
| Paste mode | not measured | 47 to 262 |

What the measurements found, in case the same ground is covered again:

- The UI system walked the entire selection every frame to refresh two
  counters and the name list, about six thousand entity lookups a frame with
  a large selection. Now behind a selection signature.
- The raycast asked for sub-elements and decals unconditionally; both are
  needed only when the matching option is on.
- Overlay drawing was 36 line draws per road segment, 14400 a frame at 400
  segments. Curves are now split by their actual curvature and anything
  behind the camera is skipped.
- A road-width band instead of the outline was tried for distant segments and
  **rejected**: it covers the road rather than marking it, at any threshold.
  The outline is drawn at every distance. Do not try this again.
