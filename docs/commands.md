# Copaste Commands & Controls

Every input and panel control in one place. Keyboard defaults are shown;
every keyboard shortcut is rebindable in Options -> Copaste -> Key bindings.
For what the features do in depth, see [features.md](features.md).

## Mouse

| Input | What it does |
|---|---|
| Left click on an object | Selects it (white ring on hover, blue when selected) |
| Left click on empty ground | Clears the selection |
| Shift + left click | Adds the object to the selection, or removes it if already selected |
| Ctrl + left click | Picks props buried inside other objects or buildings that a normal click cannot reach; clicking the same spot again cycles through everything piled up there. Shift+Ctrl adds the pick to the selection instead of replacing it |
| Left click on empty ground + drag | Marquee: a camera-aligned box selects everything inside, filtered by the Selection chips. Shift adds to the existing selection |
| Left click on a selected object + drag | Moves the whole selection; layout and heights are preserved |
| Alt + left click on an object + drag | Moves only that one object, the rest of the selection stays put |
| Hold right mouse button + drag | Rotates the selection (or the paste preview) around its center; hold Alt to snap to 45 degree steps |
| Alt + mouse wheel | Spins every selected object around its own axis, 15 degrees per notch |
| Quick right click | Steps back: cancels paste mode, pick modes and relocate, or exits the tool |

## Keyboard (defaults)

| Key | Command | What it does |
|---|---|---|
| Ctrl+Shift+C | Toggle tool | Turns Copaste on or off (same as the toolbar button) |
| Ctrl+C | Copy | Copies the selection to the clipboard with layout, heights and colors |
| Ctrl+V | Paste | Enters paste mode: a ghost preview follows the mouse, each left click stamps a copy |
| Ctrl+Z | Undo | Reverts the last action (move, rotate, align, height, nudge, delete, paste, relocate), up to 32 steps |
| Ctrl+Y | Redo | Re-applies the last undone action, deletes and pastes included |
| Tab | Relocate | The selected building follows the cursor and snaps to the road; click places it, Tab, right click or Escape puts it back (needs exactly one finished building selected) |
| Del | Delete | Removes the whole selection at once, undo brings everything back |
| PgUp / PgDn | Raise / Lower | Moves the selection (or the paste preview) up and down; hold for continuous movement |
| End | Snap to ground | Drops the selection back onto the terrain; in paste mode it resets the height offset |
| Home | Match height | Arms height picking: the next object you click sets the height for the whole selection |
| T | Type filter | Locks the marquee to the prefab type of the hovered or selected object; press again to clear |
| Ctrl + arrow keys | Nudge | Fine-positions the selection in small steps, relative to the camera |
| [ and ] | Align gap | While an align button is lit, shrinks or grows the gap of the last align live |

## Panel

| Control | What it does |
|---|---|
| Selection chips (Props, Trees, Decals, Surfaces, Buildings) | Decide what selection grabs; any mix works. Right-click a chip to solo it, right-click the solo chip again to bring all back. Buildings are off by default |
| Building elements switch | When on, selection also reaches things owned by buildings (their props, trees, decals and lot surfaces), each still following its chip. When off, nothing building-owned can be selected |
| Selected props list | Selections up to 50 objects listed by name; hovering a row rings that object in the world, clicking a row keeps only it selected |
| Copy / Paste / Save | Same as Ctrl+C / Ctrl+V; Save stores the selection as a blueprint |
| Paste look (Original / Random) | Original keeps the copied colors and, for buildings, reproduces the source lot exactly; Random lets the game roll new variations |
| Road snap switch | Pasted and relocated buildings glide along the nearest road and face it; off = free placement (visible while the Buildings chip is on) |
| Clear | Empties the clipboard |
| Undo / Redo | Same as Ctrl+Z / Ctrl+Y |
| Relocate | Same as Tab; lit while relocating, clicking it again cancels |
| Delete | Same as Del |
| Ground / Match H | Same as End / Home |
| Rotate 45 left / right | Rotates the selection (or the paste preview) in 45 degree steps around its center |
| Line / To prop / Circle | Align tools: a straight row with equal gaps, a row through a reference prop you click, or an even circle. The stepper next to them sets the gap in exact meters |
| Blueprints list | Click a name to load it and start pasting; the pencil renames inline, the x deletes the file. Lists are paged, 5 per page |
