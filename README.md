# Copaste

A copy & paste tool for props in **Cities: Skylines II** — select groups of props, copy them, and stamp them anywhere with preserved layout, rotation, and height.

## Features

- **Click & marquee selection** — click individual props, or drag a camera-aligned box on the ground to select whole groups at once (live highlight while dragging)
- **Copy & paste with preview** — the copied group follows your mouse as a ghost preview; click to place, as many times as you like
- **Layout preserved** — relative positions, rotations, and heights above terrain are kept exactly as in the original group
- **Group rotation** — hold right mouse button and drag to rotate a selection (or the paste preview) around its center
- **Move** — drag a selected prop to move the whole selection, layout and heights preserved
- **Delete** — remove the whole selection with one key
- **Paste onto roads & paths** — the group lifts to the surface you point at
- **Anarchy-style pasting** — placement errors (overlapping items etc.) are ignored while pasting (toggleable in options); pasted props are protected from being hidden by the game when the [Anarchy](https://mods.paradoxplaza.com/mods/74604/Windows) mod is installed
- **Undo** — Ctrl+Z reverts the last action (move, rotate, height change, delete, paste), up to 32 steps back
- **Blueprints** — save the copied group to disk and reuse it in any city or save; managed from the in-game panel
- **Select same** — one key selects every free-standing prop of the same type as the hovered one
- **Nudge & snap** — fine-position the selection with Ctrl+arrows, drop it back to terrain with End
- **Toolbar button + status panel** — a top-left toolbar button toggles the tool; a small panel shows the current mode, selection and clipboard counts, shortcut hints, and your blueprints
- **Rebindable hotkeys** — all shortcuts can be changed in Options → Copaste
- **English and Serbian** options localization

## Controls (defaults)

| Action | Input |
|---|---|
| Toggle tool | **Ctrl+Shift+C** or the toolbar button |
| Select a prop | **Left click** (white circle on hover, blue when selected) |
| Add/remove from selection | **Shift + left click** |
| Marquee selection | **Left click on empty ground + drag** (Shift adds to selection) |
| Move selection | **Left click on a prop + drag** — the whole selection follows the mouse |
| Clear selection | **Click on empty ground** |
| Copy selection | **Ctrl+C** |
| Paste (preview follows mouse) | **Ctrl+V**, then **left click** to place — repeat to stamp multiple copies |
| Rotate selection / paste preview | **Hold right mouse button + drag** |
| Raise / lower selection or paste preview | **Page Up / Page Down** (hold for continuous) |
| Delete selection | **Delete** |
| Undo last action | **Ctrl+Z** (works in paste mode too — removes the last stamp) |
| Type filter ("Same") | **T** — toggles a filter taken from the hovered/selected prop: while active, marquee only picks that prop type; press again to clear |
| Match height | **Home** — arms height picking: the next prop you click sets the height for the whole selection |
| Nudge selection | **Ctrl + arrow keys** (camera-relative, hold for continuous) |
| Snap selection to ground | **End** (in paste mode: resets the height offset) |
| Save / load blueprints | **Panel buttons** — Save stores the selection/clipboard; clicking a name loads it and immediately starts pasting; ✎ renames, ✕ deletes |
| Back / clear / exit tool | **Quick right click** |

Blueprints are stored as text files in `...\Colossal Order\Cities Skylines II\ModsData\Copaste\Blueprints\` — rename or delete them freely. Props from missing mods are skipped on load.

Buildings and vehicles are intentionally not selectable — this is a prop tool. Marquee selection picks up free-standing props only (props belonging to buildings are skipped to avoid grabbing hundreds of built-in sub-props); individual clicks can still select building-attached props.

## Safety limits

To keep the game responsive, selection is capped at **1000 props** and selection circles are drawn for at most 400 at a time (everything selected is still highlighted). The tool guards its own update loop, so an unexpected error disables the current action instead of crashing the game.

## Building from source

The project compiles against the game's own assemblies — no official toolchain required (see `Copaste.csproj`, `<GamePath>` property):

```powershell
MSBuild.exe Copaste.csproj -nologo -restore:false
```

The build auto-deploys `Copaste.dll`, `Copaste.mjs`, and `copaste.svg` to the game's local `Mods\Copaste` folder. Note: the code deliberately avoids `SystemAPI`, Burst jobs, and other source-generated Unity.Entities features, since it is built without IL post-processing.

## Credits & thanks

This mod stands on the shoulders of the CS2 modding community. Sincere thanks to:

- **[yenyang](https://github.com/yenyang)** — the selection, highlighting, and raycasting patterns were learned from the MIT-licensed sources of *Better Bulldozer*, *Anarchy*, *Tree Controller*, and *Move It*; Anarchy's `PreventOverride` component is integrated at runtime when present
- **[algernon](https://github.com/algernon-A)** — the object placement approach (the game's definition pipeline) was learned from the Apache-2.0 sources of *Line Tool*
- **[Bruceyboy24804](https://github.com/Bruceyboy24804)** — *Node Controller*'s UI module served as the reference for how mod UI integrates with the game's interface
- **Colossal Order & Paradox** — for Cities: Skylines II and the [official modding documentation](https://cs2.paradoxwikis.com/Modding)
- The **Cities: Skylines Modding community** for keeping their mods open source — none of this would have been possible otherwise

### Special thanks

A very special thank-you to **[Biffa](https://www.youtube.com/@BiffaPlaysCitiesSkylines)** and **[ConflictNerd](https://www.youtube.com/@ConflictNerd)** — simply for being the author's favorite Cities: Skylines YouTubers and the reason this game never gets boring. Keep the traffic flowing! 🍵

## License

MIT — see [LICENSE](LICENSE).
