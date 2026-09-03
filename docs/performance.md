# Performance

Copaste is measured, not estimated. During development an instrumented
build times the mod's two update loops, bucketed by what the user is doing,
and the individual sections inside them — the raycast, the hover pick,
overlay drawing, deferred work, the box-select scan. Rounds are repeated
across many build, play and measure cycles until the numbers stop moving.
The instrumentation is not part of the released mod.

The 1.2.0 round ran several dozen such iterations.

## Where the time goes

Microseconds per frame, measured in a real city. One frame at 60 fps is
16667 µs.

| What you are doing | Before | After |
|---|---|---|
| Tool switched off | 3.6 | 3.2 |
| Tool open, nothing selected | ~200 | 94 |
| Up to 100 objects selected | 2371 | ~2100 |
| Over 100 objects selected | 6568, spikes to 21424 | ~3000, spikes ~4500 |
| Paste preview on the cursor | 47 to 262 | 47 to 262 |

With the tool switched off its update loop does not run at all, so having
Copaste installed and unused costs about **0.02% of a frame**. That is the
number that matters for most players most of the time.

The spikes past a full frame in the fourth row are what shows up as stutter,
and they are gone.

## What was optimized

- **The panel** walked the entire selection every frame to refresh two
  counters and the name list — several thousand entity lookups per frame with
  a large selection. Those values are now recomputed only when the selection
  actually changes, behind a check that reads nothing from the game.
- **The raycast**, the single most expensive thing the tool does while it is
  open, asked the game for building sub-elements and decals on every frame
  regardless of settings. It now requests only the layers the current filters
  can actually select, which on default settings removes the descent into
  every building near the cursor.
- **Overlay drawing** subdivided every curve into twelve pieces, including
  straight ones, and drew shapes for objects behind the camera. Curves are now
  subdivided according to their actual curvature and off-screen shapes are
  skipped. The outline itself is unchanged: the savings are entirely in work
  that was never visible.
- **Saving a setting** reloaded the game's whole localization dictionary,
  which made switching a selection filter stutter. It now reloads only when
  the language actually changes.
- **The box-select scan** is throttled to run only when the cursor has moved,
  and rejects most of the map with plain arithmetic before touching any
  component data.

## Limits you control

Three sliders in Options set how much work the mod is allowed to do:
selection size (default 1000), how many selection outlines are drawn at once
(default 400) and the length of the Selected props list (default 50). Lower
them on a weaker machine, raise them if yours has room.

## Ongoing

Optimization is not a one-off pass. The instrumented build is brought back
whenever a feature touches a path that runs every frame, and anything that
shows up as a cost gets measured before it gets changed.
