# UI (cohtml / Gameface)

The in-game UI is `ui/Copaste.mjs` + `ui/Copaste.css`, rendered by the game's
embedded **Coherent Gameface** engine — an HTML/CSS/JS subset, *not* a browser.
There is no build step: the `.mjs` is plain JavaScript shipped as-is.

## Module structure

The game imports the module and calls the default export with a
`moduleRegistry`:

- `moduleRegistry.append("GameTopLeft", CopasteButton)` — the toolbar button
- `moduleRegistry.append("Game", CopastePanel)` — the floating panel
- Everything comes from **globals**: `window.React`, `window["cs2/api"]`
  (`bindValue`, `trigger`, `useValue`), `window["cs2/ui"]` (`Tooltip`,
  `Button`). **Never use `import` in the .mjs** — the game's module loader does
  not resolve bare imports; missing globals are logged and the module bails.

### Toolbar button

Uses the vanilla `Button` from `cs2/ui` with `variant: "floating"` — the same
pattern Traffic and Node Controller use. No custom classes: the game (or theme
mods like Redesigned Top Buttons) fully controls its look. The icon is our SVG.

### Panel

A single `CopastePanel` component: header (drag handle), stats card
(Selected/Clipboard counters + conditional Prop-name and Filter rows),
"Selected props" paged list, Clipboard/Edit/Rotate&Align cards, Blueprints
paged list, hint footer. All state arrives through value bindings; all actions
leave through triggers.

The panel is draggable by its header: mouse deltas are applied to `top/left`
in **pixels**, and the final position is persisted via the `setPanelPos`
trigger into mod settings (clamped to ≥ 0; `-1` is the "unset" sentinel that
means "use the CSS default position").

## Binding & trigger inventory

Value bindings (C# → UI), group `copaste`:

| Name | Type | Meaning |
|---|---|---|
| `version` | string | assembly version for the header |
| `toolActive` | bool | panel visibility |
| `pasteMode` | bool | Paste button glow + hint text |
| `selectedCount`, `clipboardCount`, `undoCount` | int | counters / enablement |
| `blueprints` | string | newline-separated names |
| `sameFilter` | string | active type filter name ("" = off) |
| `heightPickArmed`, `alignPickArmed` | bool | pick-mode glow + hints |
| `selectedName` | string | prop name when exactly one prop is click-selected |
| `panelX`, `panelY` | int | saved panel position (px; −1 = default) |
| `randomVariation` | bool | Original/Random paste toggle |
| `alignGapLive` | float | current session gap (−1 = no session) |
| `alignSessionSource` | int | 0 none / 1 Line / 2 To prop / 3 Circle |
| `selectionList` | string | `idx:ver:name` lines for selections of 2–15 |

Triggers (UI → C#): `toggleTool`, `actionCopy`, `actionPaste`, `actionDelete`,
`actionUndo`, `actionSelectSame`, `actionSnapGround`, `actionMatchHeight`,
`actionRotate(int degrees)`, `actionHeight(int steps)`,
`actionAlignLine(gap)`, `actionAlignRef(gap)`, `actionAlignCircle(gap)`,
`adjustAlignGap(int dir)`, `setAlignGapLive(gap)`, `setRandomVariation(bool)`,
`saveBlueprint`, `loadBlueprint(name)`, `deleteBlueprint(name)`,
`renameBlueprint("old\nnew")`, `setTyping(bool)`, `setPanelPos("x,y")`,
`focusProp("idx:ver")`, `selectOnlyProp("idx:ver")`.

Gap payloads are strings; C# `ParseGap` accepts `.` or `,` decimals and treats
anything unparseable/≤0 as "auto" (−1).

## Typing guard

Any focusable input (blueprint rename, gap stepper) fires
`setTyping(true/false)` on focus/blur. The tool checks `m_UiTyping` before
reacting to raw keys, otherwise typing "8" into the gap field would also
trigger hotkeys.

## Gameface constraints (learned in production)

These are the ones that actually bit us — treat them as law:

1. **SVG needs explicit dimensions.** Every SVG must carry
   `width="100%" height="100%"` (plus `viewBox`); without them Gameface renders
   the image tiny regardless of CSS on the `<img>`.
2. **No styled scrollbars.** `::-webkit-scrollbar` rules are ignored — an
   `overflow-y: auto` area scrolls invisibly. That's why both panel lists use
   explicit **pagination** instead of scrollbars.
3. **Scroll containers clip block children, not bare `<button>`s.** Rows inside
   a max-height container must be wrapped in `<div>`s or the container just
   grows.
4. **Flexbox yes, grid no.** Avoid flex `gap` too — negative-margin +
   per-child margin is the reliable spacing pattern (`.copasteBtns` /
   `.copasteBtn`).
5. **`rem` is the game's scale unit** — roughly 1 px at 1080p, scaling with
   resolution/UI scale. Panel position, however, is stored in *pixels* because
   it comes from mouse coordinates.
6. Linear gradients, border-radius, opacity, transitions, `backdrop-filter`
   (use the game's `--panelBlur` variable) and `box-shadow` all work.
7. React events supported and used: `onClick`, `onChange`, `onFocus`, `onBlur`,
   `onKeyDown`, `onMouseDown`, `onMouseEnter`, `onMouseLeave`, plus
   `window.addEventListener("mousemove"/"mouseup")` for dragging.
8. Panel clicks still reach the tool's **raw** `Mouse.current` reads; the tool
   must gate click reactions on raycast validity (the tool raycast is invalid
   while the cursor is over UI).

## CSS conventions

All classes are prefixed `copaste`. Cards (`copasteCard`) hold sections;
buttons share `copasteBtn` with modifier classes
(`copasteBtnPrimary` — green solid, `copasteBtnDanger` — red tint,
`copasteBtnActive` — green glow toggle, `copasteBtnDisabled`). Icons inside
text buttons use `copasteBtnIcon` (11 rem). The stepper
(`copasteStepper*`) is reused as the shared pager for both lists.
