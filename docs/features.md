# Copaste Feature Reference

The complete list of everything Copaste does, in one place. For a quick
overview and installation, see the [main README](../README.md); for how the
code works, see the rest of [docs/](README.md).

## Selection

- **Click** selects a single object (white ring on hover, blue when selected).
- **Shift + click** adds or removes objects from the selection.
- **Marquee**: click on empty ground and drag a camera-aligned box; everything
  inside is selected with live highlight while dragging. Shift adds to the
  existing selection.
- **Ctrl + click** picks props buried inside other objects or buildings that a
  normal click cannot reach; repeated Ctrl+clicks on the same spot cycle
  through everything piled up there. Shift+Ctrl adds the pick to the selection.
- **Selection filters**: five chips in the panel (Props, Trees, Decals,
  Surfaces, Buildings) decide what selection grabs. Any combination works.
  Right-click a chip to solo it; right-click the solo chip again to bring all
  filters back. Buildings are off by default, so the classic prop workflow is
  untouched until you opt in.
- **Building elements** switch: when on, selection also reaches things owned
  by buildings - their props, trees, decals and lot surfaces, each still
  following its filter chip. When off, nothing building-owned can be selected,
  not even by click. Vehicles and citizens are never selectable.
- **Type filter (T)**: press T on a hovered or selected object to lock the
  marquee to that exact prefab type; clicking another object switches the
  filter to its type, pressing T again clears it. Works for trees and
  surfaces too.
- **Selected props list**: selections of up to 50 objects get a paged list in
  the panel showing every object by name. Hovering a row rings that object in
  the world; clicking a row keeps only it selected. Long names trim with an
  ellipsis and show in full on hover.
- Selection is capped at 1000 objects to keep the game responsive.

## Clipboard and paste

- **Copy (Ctrl+C)** stores the selection with relative positions, rotations,
  heights above terrain, color variations and custom colors.
- **Paste (Ctrl+V)** enters paste mode: the group follows the mouse as a ghost
  preview showing real colors. Click stamps a copy; keep clicking to stamp
  more. A quick right-click steps back out.
- **Paste look**: with "Original" (default), pasted props keep the source's
  color variation and any custom color from the game's Customization tab - a
  blue bench stays blue. With "Random", the game rolls new variations per
  stamp. For buildings, "Original" also reproduces the source lot exactly
  (see Buildings below).
- **Road snap**: with the toggle on, a pasted building glides along the
  nearest road and faces it exactly like hand plopping; the rest of the group
  follows. Turn it off for free placement.
- **Anarchy-style pasting**: placement errors (overlaps, collisions) are
  ignored while pasting (toggleable in Options). When the Anarchy mod is
  installed, pasted props are also protected from being hidden by the game.
- **Clear** button next to the Clipboard counter empties the clipboard.

## Buildings

Available since 1.1.0, behind the Buildings filter chip.

- **Move** buildings by dragging - driveways, pavements, purchased upgrades
  and attached props travel along as one piece, and the game reconnects
  everything to the road automatically.
- **Rotate** with right-drag (Alt snaps to 45 degrees), nudge with
  Ctrl+arrows, spin is skipped for buildings.
- **Relocate (Tab or panel button)**: the selected building follows the
  cursor with live road snapping, exactly like plopping a new one. Click
  places it, Tab, right-click or Escape cancels and puts it back.
- **Height tools** work on buildings: PgUp/PgDn move the whole lot, End drops
  it to the surrounding terrain, Match H levels it with a reference object.
- **Copy and paste** whole groups of buildings. Pasted buildings finish
  construction instantly and come complete with pavements, driveways and
  attached props. With Paste look "Original", the copy's lot surfaces are
  exact copies of the source's - same paths, same front walkway, and surfaces
  you deleted on the original stay deleted on the copy.
- **Delete** works like the game's bulldozer, with undo: the building is
  rebuilt complete with its lot. It comes back as a fresh building though -
  residents and workers are not simulated back.
- Buildings under construction can be deleted (with undo) but not moved.
- **Rearranged and deleted building props stick**: a bench you moved across
  the yard keeps its spot, and a prop you deleted stays deleted for the whole
  session, even when the building is moved or relocated.
- **Lot decoration surfaces**: some lot clutter (clotheslines and similar) is
  respawned randomly by the game and cannot be removed piece by piece. The
  supported way is deleting its decoration surface (Building elements +
  Surfaces filters on): the clutter goes with it, permanently, and nothing is
  written into your save.

## Painted surfaces

- Select surfaces by click or marquee (polygon outline shows what is
  selected), behind the Surfaces filter chip.
- Surfaces move, nudge and rotate together with the group, copy and paste
  with it, and delete with undo. Pasted and repainted surfaces stay fully
  editable with the game's own surface tool.
- Building-owned lot surfaces can be selected and deleted (with undo) behind
  the Building elements switch; they never move individually - the building
  moves them as part of its lot.

## Editing

- **Move**: drag any selected object and the whole selection follows the
  mouse, layout and heights preserved.
- **Group rotation**: hold the right mouse button and drag to rotate the
  selection (or the paste preview) around its center; hold **Alt** while
  dragging to snap to 45 degree steps. The panel also has 45 degree left and
  right buttons.
- **Alt + drag**: pulls a single object out of the selection and moves only
  it - the rest stays put. Handy for nudging one prop of a group into place.
- **Alt + mouse wheel**: spins every selected object around its own axis, 15
  degrees per notch - great after a Line align to vary the facing.
- **Height**: PgUp/PgDn raise and lower the selection (hold for continuous),
  End drops it back to the terrain, Home arms height picking - the next
  object you click sets the height for the whole selection.
- **Nudge**: Ctrl+arrows fine-position the selection relative to the camera.
- **Undo (Ctrl+Z)**: reverts the last action - move, rotate, align, height,
  nudge, delete, paste or relocate - up to 32 steps. Deleted trees come back
  with their growth stage and colors.
- **Redo (Ctrl+Y)**: re-applies the last undone action, deletes and pastes
  included.
- **Align: Line** turns a messy selection into a straight row with equal gaps
  and uniform rotation. **To prop** does the same anchored to a reference
  prop you click. **Circle** arranges the selection evenly on a circle.
  While an align button is lit, [ and ] keys or the panel stepper resize the
  layout live; the gap can be typed in exact meters.
- **Delete (Del)** removes the whole selection at once.

## Controls

Every mouse input, keyboard shortcut and panel control is listed with an
explanation in [commands.md](commands.md). All keyboard shortcuts are
rebindable in Options.

## Blueprints

- **Save** stores the current selection to disk as a text file; blueprints
  work in any city and any save, colors and buildings included - a blueprint
  can hold a whole mini neighborhood.
- Managed from the panel: paged list, load by click (starts pasting
  immediately), inline rename, delete.
- Files live in `...\Colossal Order\Cities Skylines II\ModsData\Copaste\Blueprints\`
  and can be renamed, deleted or shared freely. Objects from missing mods and
  assets are skipped on load; files from older Copaste versions still load.

## Panel

- A top-left toolbar button toggles the tool; the panel is draggable by its
  header and remembers its position between sessions.
- Cards: counters and name row, Selection filters, Selected props list,
  Clipboard with Paste look and Road snap, Edit (Undo, Redo, Relocate,
  Delete), Align with the gap stepper, Blueprints, and a hint footer that
  follows what you are doing.

## Input and languages

- **Every hotkey is rebindable** in Options -> Copaste -> Key bindings,
  grouped by purpose (Tool, Clipboard, Editing, Nudge, Align).
- The Options screen is localized in **English, German, French and Serbian**;
  the in-game panel itself is English by design (compact, near-universal
  labels).

## Safety

- **Save-game safe**: Copaste writes only the game's own vanilla components
  and stores no custom data in your save file. A city touched by Copaste
  loads identically with or without the mod, and unsubscribing leaves
  nothing behind. Undo history and session data live in memory only.
- The tool guards its own update loop: an unexpected error disables the
  current action instead of crashing the game.
- Selection and overlay limits (1000 objects, 400 selection circles) keep
  performance stable in dense areas.
