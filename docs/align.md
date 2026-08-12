# Align Tools

Three panel buttons rearrange the current selection. All of them push a single
undo record (positions *and* rotations) and start a **live align session**.

## Line

"Make a tidy row" in one click:

1. The line is defined by the two props of the selection that are farthest
   apart (ground-plane distance).
2. Every prop is projected onto that line and redistributed with **equal
   gaps** — the stepper value if set, otherwise spread evenly between the two
   end props.
3. All props are rotated to the **same orientation, perpendicular to the
   line**, choosing the side most props already face (a row of benches doesn't
   flip its back to the path).

## To prop (reference pick)

Click the button (it lights up, the hint bar changes), then click a reference
prop — which may or may not be part of the selection:

- the row passes through the reference prop, extending along its **right
  axis** (props line up side by side relative to the way it faces),
- every prop takes the reference prop's exact rotation,
- equal gaps as above.

Right-click cancels the pick. With a single selected prop there is no row to
space out, so the prop is simply rotated like the reference and snapped onto
its line (no session starts). Arming To prop disarms Match H and vice versa;
the armed state auto-clears if the selection empties.

## Circle

Distributes the selection evenly on a circle around the selection centroid,
preserving the current angular order (props keep their neighbors). The radius
comes from the average distance to the centroid, or — when a gap is set — from
`radius = gap · count / 2π`, i.e. the gap is the **arc distance** between
props. Requires 3+ props. Circle does not change rotations.

## The live align session

After Line / To prop / Circle, the session stays active until the selection is
touched (click, marquee, move, rotate, nudge, delete, filter, undo, paste mode,
tool off — all end it via `EndAlignSession()`):

- the originating button glows (`alignSessionSource`: 1 = Line, 2 = To prop,
  3 = Circle) and the section header shows the current gap ("Align · 4.5 m"),
- **`[` and `]`** (rebindable: *Align gap −/+*) shrink/grow the gap in 0.5 m
  steps,
- the panel **stepper** does the same (`adjustAlignGap` trigger routes to the
  identical method, `AdjustAlignSessionGap`),
- typing a number in the stepper and pressing **Enter** (or leaving the field)
  applies it immediately (`setAlignGapLive`),
- for Circle the gap is the arc spacing, so adjusting it grows/shrinks the
  whole circle.

Session state (`m_AlignOrder` in on-line order, origin, direction, start
angle) is captured once at align time; `ApplyAlignSession()` recomputes all
positions from the current gap, resampling terrain height and preserving each
prop's height offset. One Ctrl+Z reverts the align *including* every gap
adjustment, because only the initial align pushed an undo record.

The minimum gap everywhere is 0.1 m (initial) and the adjustment step is
0.5 m.

## Interaction with per-prop rotation

Alt+mouse-wheel spinning (see [tool-system.md](tool-system.md#rotation)) does
**not** end the align session — a common flow is: Line → Alt+wheel to angle all
props → `]` to widen the row.

## Gotchas

- Rotation writes must add `Updated` + `BatchesUpdated` themselves. The
  session's position pass skips props whose target XZ equals their current XZ
  (the first prop of a row always does), so a rotation piggy-backing on the
  position pass would never be flushed for them.
- Anything that mutates the selection **must** call `EndAlignSession()`;
  otherwise `[`/`]` would re-layout props the user already deselected. If you
  add a new selection-mutating path, add the call.
- `m_AlignSource` exists only for the UI glow; geometry runs off `m_AlignKind`
  (`Spaced` covers both Line and To prop, `Circle` is circle math in
  `ApplyAlignSession`).
