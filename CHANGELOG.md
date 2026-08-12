# Changelog

## [1.0.6] - 2026-08-12

Panel redesign and quality-of-life update.

### Panel
- Complete visual redesign: grouped cards, icons on every button, clear button states, readable counters
- The panel can now be dragged by its header and remembers its position between sessions
- Single click-selected props show their name in the panel (marquee selections don't)
- Small selections (up to 15 props) get a Selected props list in the panel: hovering an entry rings that prop in the world, clicking it keeps only that prop selected - handy when a prop is hard to click directly
- Blueprint list shows 5 entries before scrolling

### Editing
- Pasted props now keep the original's look by default — both the game's color variation and any custom color picked in the Customization tab. A blue bench stays blue, a recolored one keeps its custom color. The Original/Random toggle in the panel restores the old random behavior
- The paste preview shows the real colors too, instead of shuffling them while you move
- Undo of a delete also restores the original colors now
- New Align tools: Line makes a tidy row - straight line, equal gaps and all props rotated the same way; To prop does the same anchored to a reference prop you click (its position, facing and rotation); Circle arranges the selection evenly on a circle. The gap stepper sets exact meters; while an align button is lit, [ and ] keys (rebindable) or the stepper resize the layout live
- Alt+drag moves a single prop out of the selection; Alt+mouse wheel spins every selected prop around its own axis (15 degree steps)
- Move drag no longer jerks at the start — the selection follows the cursor smoothly from the first frame
- Nudge (Ctrl+arrows) step halved for finer control

### Blueprints
- Blueprints now store color variations and custom colors too; older blueprint files still load fine

### Options
- Mod settings are now split into General and Key bindings tabs, with key bindings grouped by what they do

## [1.0.5] - 2026-08-10

Toolbar button facelift.

- The toolbar button now uses the game's standard floating button, the same one Traffic, Node Controller and other mods use — correct icon size and the default look
- Removed the custom blue background (both idle and active) — the button now blends in with other mod buttons, and theme mods like Redesigned Top Buttons can restyle it just like the rest

## [1.0.4] - 2026-08-10

Stability update.

### Blueprints
- Fixed the big one: Save used to quietly store the old clipboard content instead of what you actually had selected. It now saves exactly what you see selected
- The Save button only appears while something is selected, so you can't save the wrong thing by accident
- Blueprints now remember tree growth stages, and older blueprint files still load fine
- Picking a different blueprint while already pasting now properly switches the preview
- Typing a blueprint name no longer sets off tool hotkeys mid-typing

### Editing
- Raised props now keep their height after being moved or nudged
- Pressing Ctrl+V in the middle of dragging a prop no longer confuses the tool
- Selections that hit the 1000 prop limit no longer leave stray highlighted props behind
- Parked vehicles can no longer be selected by mistake

### Under the hood
- Copaste no longer touches the game's error indicators on unrelated objects, and gets along better with Anarchy's own toggle
- Stricter blueprint name validation and small performance work in the panel and overlays

## [1.0.3] - 2026-08-09

- Fixed panel clicks leaking through to the map: clicking Save (or any panel button) no longer clears the selection underneath, so saving a blueprint straight from a marquee selection works now
- Undo of a paste now removes only the props that paste created; identical pre-existing props on the same spot are no longer deleted
- Fixed a stale-records issue where clicking immediately after entering paste mode could create a bogus undo entry
- Parked vehicles can no longer be selected or copied

## [1.0.2] - 2026-08-08

- Fixed marquee corner jumping onto buildings while dragging (the box now follows the terrain under the cursor)
- Marquee can no longer be started by clicking on a building

## [1.0.1] - 2026-08-08

- Marquee selection now uses each prop's footprint instead of its center point, so props visually inside the box no longer get skipped when the edge passes through them
- While the type filter is active, clicking a prop now switches the filter to that prop's type
- Match Height now aligns to the reference prop's absolute height (previously height above terrain, which looked misaligned on slopes)
- Hold ALT while rotating to snap to 45 degree steps
- ESC now exits paste mode / deactivates the tool
- Panel title now shows the mod version
- New thumbnail and screenshots

## [1.0.0] - 2026-08-08

Initial release.

- Click & marquee selection (props, decals and trees) with live highlight
- Copy & paste with ghost preview, repeatable stamping
- Move, group rotation, height controls, match height, snap to ground, nudge
- Delete with full undo (32 steps, tree growth stages preserved)
- Type filter for marquee selection
- Blueprints: save prop groups to disk and reuse them in any city or save
- Anarchy-style pasting with optional Anarchy mod integration
- Toolbar button, in-game panel with clickable actions and tooltips
- Rebindable hotkeys, English and Serbian localization
