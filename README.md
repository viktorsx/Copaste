# Copaste

A copy & paste tool for props in **Cities: Skylines II** — select groups of props, copy them, and stamp them anywhere with preserved layout, rotation, and height.

## About this mod

Copaste started as a personal tool: we wanted a group copy/paste and blueprint workflow for prop detailing that no existing tool offered, so we built one for our own city. It grew feature by feature — undo, blueprints, a proper UI — until it felt useful enough to share, so here it is.

It was built by studying the source code of the excellent open-source mods credited below. Their MIT and Apache-2.0 licenses explicitly permit exactly this kind of learning and reuse — that openness is what makes the CS2 modding scene great, and this mod is published under the same spirit (MIT). Copaste is not a replacement for Move It or ctrlC; it is a different, prop-focused workflow that can happily live alongside them.

## Features

- **Click & marquee selection** — click individual props, or drag a camera-aligned box on the ground to select whole groups at once (live highlight while dragging). Works on props, decals **and trees**
- **Copy & paste with preview** — the copied group follows your mouse as a ghost preview; click to place, as many times as you like
- **Layout preserved** — relative positions, rotations, and heights above terrain are kept exactly as in the original group
- **Group rotation** — hold right mouse button and drag to rotate a selection (or the paste preview) around its center
- **Move** — drag a selected prop to move the whole selection, layout and heights preserved
- **Delete** — remove the whole selection with one key. Combined with marquee + type filter it makes bulk cleanup trivial: box-select an area, press **T** on a tree to keep only that tree type, hit **Delete** — a whole overgrown forest gone in three clicks (Ctrl+Z brings it back, growth stages preserved)
- **Paste onto roads & paths** — the group lifts to the surface you point at
- **Anarchy-style pasting** — placement errors (overlapping items etc.) are ignored while pasting (toggleable in options); pasted props are protected from being hidden by the game when the [Anarchy](https://mods.paradoxplaza.com/mods/74604/Windows) mod is installed
- **Undo** — Ctrl+Z reverts the last action (move, rotate, height change, delete, paste), up to 32 steps back; deleted trees come back with their original growth stage
- **Blueprints** — save the copied group to disk and reuse it in any city or save; managed from the in-game panel
- **Type filter ("Same")** — press T to toggle a filter taken from the hovered/selected prop: marquee then only picks that exact type (works for trees too)
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

The project uses the [official CS2 modding toolchain](https://cs2.paradoxwikis.com/Modding_Toolchain) (install it via the game's launcher first — it sets up the `CSII_TOOLPATH` environment and `Mod.props`/`Mod.targets`):

```powershell
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'   # ModPostProcessor targets the .NET 6 runtime
dotnet build Copaste.csproj -c Release
```

The build runs the game's IL post-processor, produces Burst native libraries for Windows/Linux/macOS, and auto-deploys everything (DLL + UI files) to the game's local `Mods\Copaste` folder.

Two quirks worth knowing if you hack on this:
- `EntityManager.CreateEntity(archetype)` does not compile under the toolchain's `net48` profile (missing span types — the official project template has the same issue); use the `CreateEntity(archetype, count, allocator)` overload instead.
- The hand-written UI module (`ui/Copaste.mjs`) uses the game's runtime globals (`window.React`, `window["cs2/api"]`) instead of imports — ES imports silently break UI modules.

## Credits & thanks

This mod stands on the shoulders of the CS2 modding community — it was written from scratch, but the patterns and APIs were learned from open-source mods whose licenses (MIT, Apache-2.0) explicitly welcome that. Sincere thanks to:

- **[yenyang](https://github.com/yenyang)** — the selection, highlighting, and raycasting patterns were learned from the MIT-licensed sources of *Better Bulldozer*, *Anarchy*, *Tree Controller*, and *Move It*; Anarchy's `PreventOverride` component is integrated at runtime when present
- **[algernon](https://github.com/algernon-A)** — the object placement approach (the game's definition pipeline) was learned from the Apache-2.0 sources of *Line Tool*
- **[Bruceyboy24804](https://github.com/Bruceyboy24804)** — *Node Controller*'s UI module served as the reference for how mod UI integrates with the game's interface
- **Colossal Order & Paradox** — for Cities: Skylines II and the [official modding documentation](https://cs2.paradoxwikis.com/Modding)
- The **Cities: Skylines Modding community** for keeping their mods open source — none of this would have been possible otherwise

### Special thanks

A very special thank-you to **[Biffa](https://www.youtube.com/@BiffaPlaysCitiesSkylines)** and **[ConflictNerd](https://www.youtube.com/@ConflictNerd)** — simply for being the author's favorite Cities: Skylines YouTubers and the reason this game never gets boring. Keep the traffic flowing! 🍵

## License

MIT — see [LICENSE](LICENSE).
