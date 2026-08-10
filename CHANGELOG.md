# Changelog

## [1.0.4] - 2026-08-09

- Blueprints now save tree growth stages (older blueprint files still load fine)
- Loading a blueprint while already pasting now correctly refreshes the preview to the new group
- Pressing Ctrl+V in the middle of dragging a prop no longer leaves the tool in a broken move state
- Raised props now keep their height reliably after being moved or nudged
- Selection overflowing the 1000-prop limit no longer leaves extra props permanently highlighted
- Copaste no longer suppresses the game's error indicators on unrelated objects while pasting, and plays nicer with Anarchy's own error toggle
- Blueprint names are validated more strictly
- Small performance improvements in the panel and selection overlays

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
