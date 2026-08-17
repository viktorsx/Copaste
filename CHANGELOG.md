# Changelog

## [1.1.0] - 2026-08-17

Buildings update.

### Buildings
- New Selection card in the panel with five independent filters: Props, Trees, Decals, Surfaces and Buildings. Any mix works — grab only buildings and surfaces, only trees, everything at once, whatever the job needs. Buildings are off by default so the classic prop workflow stays untouched
- Building elements switch in the Selection card: when on, selection also reaches things that belong to buildings — props, trees, decals and lot surfaces, each following its filter. When off, nothing building-owned can be selected, not even by click. Copying is smart about it — a copied building brings its own props, so they are never duplicated
- Deleting a building's lot decoration surface (with undo) also removes whatever it keeps spawning there — the clean way to permanently get rid of regenerating lot clutter like clotheslines, with nothing written into the save. Yard benches, chairs and bins are ordinary building props — select and delete them directly
- Paste look "Original" now applies to lots too: the pasted building's lot surfaces are exact copies of the source's — same paths, same front walkway, deleted surfaces stay deleted, reshapes carry over — instead of a fresh random factory roll. "Random" keeps the game's roll. Undo/redo of a deleted building restores its lot exactly, and blueprints carry the lot as well
- Copy and paste whole groups of buildings with their layout. Pasted buildings finish construction instantly and come complete with their pavements, driveways and attached props
- Painted surfaces are part of the selection too: box select outlines them, they copy, rotate and paste with the group, and stay fully editable afterwards
- Blueprints can now store mini neighborhoods: buildings, props and painted surfaces together
- Undo removes a pasted group cleanly, buildings and surfaces included
- Buildings can now be moved like props: drag them, rotate with right drag, nudge with Ctrl+arrows, spin with Alt+wheel. Driveways, pavements and installed upgrades travel along, and the game reconnects everything to the road on its own
- Painted surfaces in the selection move, nudge and rotate together with the group
- Undo and redo cover building and surface moves; buildings under construction sit still until finished
- Selected and hovered buildings show their lot rectangle instead of a big circle
- Finishing a move or rotation sends the game one clean final update so road connections re-evaluate right away — no stuck "No car access" warnings
- Height tools work on buildings too: PgUp/PgDn raise and lower a building with its whole lot, End drops it back to the terrain, Match H levels it with a reference — handy on rough terrain
- Props you rearranged on a building (a bench moved across the yard) keep their custom spots when the building moves — the game used to snap them back to the stock layout
- Props you deleted from a building stay deleted when the building moves — the game used to quietly rebuild them from the stock layout
- Building drags are much smoother now: heavy game work (terrain leveling, road reconnection, prop layout) runs on a short interval and once cleanly at the end of the drag, instead of every frame fighting the mouse
- Painted surfaces can now be selected with a plain click too (Shift adds or removes them from the selection), not just with box select
- Delete now removes selected painted surfaces, and undo repaints them exactly as they were; copying works with a surface-only selection as well
- Road snap for pasting: with the new toggle on, a pasted building glides along the nearest road and faces it exactly like normal plopping — the rest of the group follows. Turn it off for free placement
- New Relocate button (keybind: **Tab**, rebindable): select a building and it follows the cursor with road snap, click to place, Tab, right click or Escape to cancel — the fast way to move one house without dragging

### Panel
- Panel layout refresh: new COPASTE logo in the header with the version tucked to the right, a Selection card with the five filters, the type filter now lives as an always-visible row with its own button, Paste look sits under Clipboard, and Ground/Match H joined the rotate and align tools in one Align card
- Long prop and building names no longer overflow their row — they trim with "..." and hovering the row shows the full name
- Buttons that cannot act on the current selection (align tools with only buildings or surfaces selected) now show as disabled instead of silently doing nothing
- Delete works on buildings too, exactly like the game's bulldozer. Undo rebuilds the deleted building complete with its pavements and driveways — as a fresh building though: residents and workers don't come back
- Options localization added for German and French (joining English and Serbian)

### Editing
- Redo (Ctrl+Y): brings back the last undone action — moves, rotations, aligns, heights, deletes and pastes alike; Redo button in the panel too
- A small clear button next to the Clipboard counter empties the clipboard

## [1.0.6] - 2026-08-12

Panel redesign and quality-of-life update.

### Panel
- Complete visual redesign: grouped cards, icons on every button, clear button states, readable counters
- The panel can now be dragged by its header and remembers its position between sessions
- Single click-selected props show their name in the panel (marquee selections don't)
- Small selections (up to 15 props) get a Selected props list in the panel: hovering an entry rings that prop in the world, clicking it keeps only that prop selected - handy when a prop is hard to click directly
- Blueprint and selected props lists are paged: 5 per page with arrows in the section header

### Editing
- Pasted props now keep the original's look by default — both the game's color variation and any custom color picked in the Customization tab. A blue bench stays blue, a recolored one keeps its custom color. The Original/Random toggle in the panel restores the old random behavior
- The paste preview shows the real colors too, instead of shuffling them while you move
- Undo of a delete also restores the original colors now
- New Align tools: Line makes a tidy row - straight line, equal gaps and all props rotated the same way; To prop does the same anchored to a reference prop you click (its position, facing and rotation); Circle arranges the selection evenly on a circle. The gap stepper sets exact meters; while an align button is lit, [ and ] keys (rebindable) or the stepper resize the layout live
- Alt+drag moves a single prop out of the selection; Alt+mouse wheel spins every selected prop around its own axis (15 degree steps)
- Ctrl+click cycles through props around the click point - picks up props partially buried in other objects or buildings that a normal click can never reach; click the same spot again to go to the next one
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
