# Changelog

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
