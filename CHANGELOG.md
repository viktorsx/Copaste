# Changelog

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
