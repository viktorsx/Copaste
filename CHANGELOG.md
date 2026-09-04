# Changelog

## [1.2.1] - 2026-09-04

### Fixes
- **Moving a node on an elevated road no longer splits it.** The height of an elevated road is a property of the road, not of the ground beneath it; dragging a node across a slope used to carry the bridge with the terrain, leaving a step at the joint and, at that step, a junction traffic would not cross. Ground-level roads still follow the terrain as before
- A joint at the far end of a moved segment is now rebuilt together with every road meeting it, so a road that was not touched can no longer keep an outdated joint shape

## [1.2.0] - 2026-09-03

Fences & Networks update.

### Curve bending
- Select exactly one fence or one road segment and handles appear: two on its ends, and two control points off to the side, each tied to its end by a thin line. Dragging an end moves it while the rest keeps its shape (a fence end also re-links its chained neighbor, a road end keeps its junction updated); dragging a control point reshapes that half of the curve. Undo restores the whole curve
- A click **selects** a handle first (it turns green) and only a press on the already-selected handle starts dragging, so an ordinary click can never bend the road by accident. PgUp/PgDn move the selected handle, which is how a sloped fence or a raised arc is made
- **Grab the segment anywhere and bend it**: press on a selected segment's curve wherever you like and pull — it bends under your hand, and the movement is shared between both control points so the shape follows instead of whipping
- The control points follow the mouse one to one. They used to sit on the curve itself and be solved for, which moved them more than twice as far as the cursor and made a smooth result hard to reach
- Guide snapping is measured on screen rather than in meters, so it helps when zoomed out and gets out of the way when zoomed in for fine work, and it holds once caught instead of flickering at the edge
- **Alt while dragging a fence joint** slides it along the straight line between its two neighbors, the same as Alt on a road node; the chain's outer end slides along its own link's line instead, which extends or shortens the fence without bending it
- **Lane alignment**: with one road segment selected, a triangle sits next to each joint between two segments. Clicking it cycles the joint through center, left and right alignment — on a lane transition the through lanes line up exactly, which is the classic highway exit look in one click. On streets with sidewalks the targets come from the real driving lanes; on roads without them the roadway edges align

### Networks
- New Networks filter in the Selection card: road, path and track nodes and segments can be selected (click or box select) and **moved, rotated and nudged** — junctions keep their smooth curves, connected roads stretch to follow, pillars travel along, and the game reconnects buildings and traffic on its own
- Box select grabs a segment as soon as the box touches its curve — no need to fit both junctions inside
- Undo and redo restore moved networks exactly
- Grab and drag directly: press a selected node, segment, fence or painted surface and pull — no prop needed in the selection
- **Roads copy and paste**: selected segments go to the clipboard with their upgrades (tree rows, wide sidewalks...), paste shows the game's own ghost preview, pasted pieces weld back into one network — intersections stay intersections — and form junctions with existing roads; undo removes the stamp cleanly, and blueprints store road segments alongside everything else
- **Delete works on networks**: selected segments are removed the way the bulldozer would, a selected node takes its connecting roads with it, and undo rebuilds the piece welded together, upgrades included
- **Tap Alt to straighten**: a selected middle node (or a run of them) snaps onto the straight line between its neighbors and the road through it straightens out — junctions and dead ends stay put
- **Junction state travels with the copy**: roundabouts, manual traffic lights and stop signs are captured with the roads and rebuilt on the paste, saved into blueprints, and undo of a deleted junction brings them back too
- **Redo** re-applies an undone road paste or deletion, including pieces the game splits at tunnel portals and retaining walls
- Moving a junction **keeps the lane alignment** of every road meeting it: an exit that was lined up by hand stays lined up instead of snapping back to center
- **Alt while dragging a single selected node** slides it along a straight line: a middle node rides the line between its two neighbors, an end node rides the continuation of the road, which straightens a crooked final segment
- **Underground mode (U, or the button next to the counters)**: the view switches to the game's underground look and selection reaches only what is below ground, so a box over a metro tunnel cannot grab the trees above it, and vice versa. Copy, paste and undo work the same in both worlds
- Power lines and pipes stay untouched, and creating networks from scratch stays out of scope

### Fences
- New Fences filter in the Selection card: standalone fences and hedges (the ones drawn along a line) can now be selected with a click or box select, shown with a line along their curve. Off by default
- Fences copy and paste with the group, ghost preview included, and keep their look. They follow the terrain when pasted or moved
- Move, rotate and nudge work on fences; chained fences stay connected - moving one link stretches its neighbor to keep the joint
- Delete removes a fence with its endpoints (shared joints of a chain are kept while another link still needs them); undo brings it back and reattaches it to the chain
- Blueprints store fences alongside props, buildings and surfaces
- PgUp/PgDn raise and lower fences (they hold the height on their own), End drops them back onto the terrain
- Pasted fences no longer weld themselves to nearby existing fences — each paste stays its own piece, the same way the game keeps building fences separate
- Fences owned by buildings are intentionally not selectable - the building manages those

### Blueprints
- Assets from Paradox Mods now load correctly from blueprints: lines carry the asset's identity, so a saved PDX prop, surface or fence finds its exact asset again. Older blueprint files keep working, and files stay readable by older mod versions

### Options
- The three safety limits are now sliders in Options: selection size (default 1000), selection outlines (default 400) and the Selected props list (default 50) - stronger machines can raise them
- **Mod language**: the mod can speak its own language regardless of the game's, or follow the game as before
- **Panel theme**: a Vanilla option draws the panel inside the game's own panel chrome so it blends in with the rest of the interface
- **Panel size** (80–125%) scales the whole panel, and a separate **Text size** (90–130%) grows the lettering without changing the layout
- The Options page is now split into named sections: Behavior, Panel, Limits, and the key bindings tab
- The old "Anarchy while pasting" option is now called **Ignore placement errors when pasting**, and its description spells out what it does and does not cover - the name promised more than the option ever did

### Panel
- The panel now follows the game language: everything is translated in German, French and Serbian, with English as the fallback
- The how-to hint moved from the panel's footer into the logo's tooltip, which makes the panel shorter without losing the explanation
- The underground toggle sits next to the Selected and Clipboard counters, and lights up while it is on

### Performance
- Selecting large groups is far cheaper than it was. With more than a hundred road segments selected the mod's cost per frame dropped from about 6.6 ms, with spikes past 21 ms, to roughly 3 ms with spikes around 4.5 ms; spikes of that size were what showed up as stutter
- Opening the tool with nothing selected costs about half of what it did, because the raycast now asks the game only for the layers the current filters can actually select
- The panel no longer walks the whole selection every frame to refresh its counters, which was thousands of lookups per frame with a big selection
- While the tool is switched off it does not run at all, which is unchanged and was confirmed by measurement
- Switching a selection filter no longer stutters: saving the setting was reloading the game's whole localization dictionary every time

### Fixes
- **Loading another city clears the history.** Undo and redo used to carry over from the city you just left, where the same steps meant something else entirely — an undo could remove one of the new city's own buildings, or bring back one from the old city. The clipboard is deliberately kept, so copying in one city and pasting in another still works
- Renaming a blueprint and then clicking somewhere else no longer leaves the tool deaf: clicks, Delete, undo and every shortcut kept being swallowed until the game was restarted
- Undo of a paste no longer removes things that were already there. Pasting a road over one you had built could make the game split the old road, and undo then treated the pieces as part of the stamp
- Deleting a painted surface with its plantings is reliable again; with a certain number of children it could stop partway
- Deleting more than 500 objects in one go is refused with the error sound: a one-frame removal of that size can crash the game itself. Delete in parts instead - a batched delete that lifts this limit is planned
- Relocating a tall building no longer makes it chase its own roof: when the cursor ray hits the building being carried, the target is taken from the terrain under the cursor, so the building follows the ground smoothly and road snap looks in the right place
- Road snap now reaches buildings with deep lots: the search radius grows with the lot depth, where a fixed 30 m measured from the cursor rejected exactly the buildings that need snapping most
- Alt+wheel spinning two different objects within a second no longer merges into one undo record - each selection change starts its own record, so undo restores the right object
- A blueprint whose lot-surface asset is missing on this machine now falls back to the factory lot instead of stripping the building's paths and driveway
- One broken line in a blueprint file no longer fails the whole load, and numbers with comma decimals are no longer misread a hundredfold - each bad line is skipped by itself

## [1.1.0] - 2026-08-17

Buildings update.

### Buildings
- New Selection card in the panel with five independent filters: Props, Trees, Decals, Surfaces and Buildings. Any mix works - grab only buildings and surfaces, only trees, everything at once, whatever the job needs. Buildings are off by default so the classic prop workflow stays untouched
- Building elements switch in the Selection card: when on, selection also reaches things that belong to buildings - props, trees, decals and lot surfaces, each following its filter. When off, nothing building-owned can be selected, not even by click. Copying is smart about it - a copied building brings its own props, so they are never duplicated
- Deleting a building's lot decoration surface (with undo) also removes whatever it keeps spawning there - the clean way to permanently get rid of regenerating lot clutter like clotheslines, with nothing written into the save. Yard benches, chairs and bins are ordinary building props - select and delete them directly
- Paste look "Original" now applies to lots too: the pasted building's lot surfaces are exact copies of the source's - same paths, same front walkway, deleted surfaces stay deleted, reshapes carry over - instead of a fresh random factory roll. "Random" keeps the game's roll. Undo/redo of a deleted building restores its lot exactly, and blueprints carry the lot as well
- Copy and paste whole groups of buildings with their layout. Pasted buildings finish construction instantly and come complete with their sidewalks, driveways and attached props
- Painted surfaces are part of the selection too: box select outlines them, they copy, rotate and paste with the group, and stay fully editable afterwards
- Blueprints can now store mini neighborhoods: buildings, props and painted surfaces together
- Undo removes a pasted group cleanly, buildings and surfaces included
- Buildings can now be moved like props: drag them, rotate with right drag, nudge with Ctrl+arrows, spin with Alt+wheel. Driveways, sidewalks and installed upgrades travel along, and the game reconnects everything to the road on its own
- Painted surfaces in the selection move, nudge and rotate together with the group
- Undo and redo cover building and surface moves; buildings under construction sit still until finished
- Selected and hovered buildings show their lot rectangle instead of a big circle
- Finishing a move or rotation sends the game one clean final update so road connections re-evaluate right away - no stuck "No car access" warnings
- Height tools work on buildings too: PgUp/PgDn raise and lower a building with its whole lot, End drops it back to the terrain, Match H levels it with a reference - handy on rough terrain
- Props you rearranged on a building (a bench moved across the yard) keep their custom spots when the building moves - the game used to snap them back to the stock layout
- Props you deleted from a building stay deleted when the building moves - the game used to quietly rebuild them from the stock layout
- Building drags are much smoother now: heavy game work (terrain leveling, road reconnection, prop layout) runs on a short interval and once cleanly at the end of the drag, instead of every frame fighting the mouse
- Painted surfaces can now be selected with a plain click too (Shift adds or removes them from the selection), not just with box select
- Delete now removes selected painted surfaces, and undo repaints them exactly as they were; copying works with a surface-only selection as well
- Road snap for pasting: with the new toggle on, a pasted building glides along the nearest road and faces it exactly like normal plopping - the rest of the group follows. Turn it off for free placement
- New Relocate button (keybind: **Tab**, rebindable): select a building and it follows the cursor with road snap, click to place, Tab, right click or Escape to cancel - the fast way to move one house without dragging

### Panel
- Panel layout refresh: new COPASTE logo in the header with the version tucked to the right, a Selection card with the five filters, the type filter now lives as an always-visible row with its own button, Paste look sits under Clipboard, and Ground/Match H joined the rotate and align tools in one Align card
- Long prop and building names no longer overflow their row - they trim with "..." and hovering the row shows the full name
- Buttons that cannot act on the current selection (align tools with only buildings or surfaces selected) now show as disabled instead of silently doing nothing
- Delete works on buildings too, exactly like the game's bulldozer. Undo rebuilds the deleted building complete with its sidewalks and driveways - as a fresh building though: residents and workers don't come back
- Options localization added for German and French (joining English and Serbian)

### Editing
- Redo (Ctrl+Y): brings back the last undone action - moves, rotations, aligns, heights, deletes and pastes alike; Redo button in the panel too
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
- Pasted props now keep the original's look by default - both the game's color variation and any custom color picked in the Customization tab. A blue bench stays blue, a recolored one keeps its custom color. The Original/Random toggle in the panel restores the old random behavior
- The paste preview shows the real colors too, instead of shuffling them while you move
- Undo of a delete also restores the original colors now
- New Align tools: Line makes a tidy row - straight line, equal gaps and all props rotated the same way; To prop does the same anchored to a reference prop you click (its position, facing and rotation); Circle arranges the selection evenly on a circle. The gap stepper sets exact meters; while an align button is lit, [ and ] keys (rebindable) or the stepper resize the layout live
- Alt+drag moves a single prop out of the selection; Alt+mouse wheel spins every selected prop around its own axis (15 degree steps)
- Ctrl+click cycles through props around the click point - picks up props partially buried in other objects or buildings that a normal click can never reach; click the same spot again to go to the next one
- Move drag no longer jerks at the start - the selection follows the cursor smoothly from the first frame
- Nudge (Ctrl+arrows) step halved for finer control

### Blueprints
- Blueprints now store color variations and custom colors too; older blueprint files still load fine

### Options
- Mod settings are now split into General and Key bindings tabs, with key bindings grouped by what they do

## [1.0.5] - 2026-08-10

Toolbar button facelift.

- The toolbar button now uses the game's standard floating button, the same one other mods use - correct icon size and the default look
- Removed the custom blue background (both idle and active) - the button now blends in with other mod buttons, and theme mods like Redesigned Top Buttons can restyle it just like the rest

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
