<p align="center">
 <picture>
 <source media="(prefers-color-scheme: dark)" srcset="docs/images/copaste-logo-white.svg">
 <img src="docs/images/copaste-logo-black.svg" alt="Copaste" width="440">
 </picture>
</p>

<p align="center">
 <a href="https://mods.paradoxplaza.com/mods/154371/Windows"><img src="https://img.shields.io/badge/Paradox%20Mods-Copaste-45a06c" alt="Paradox Mods"></a>
 <a href="https://github.com/viktorsx/Copaste/releases/latest"><img src="https://img.shields.io/github/v/release/viktorsx/Copaste?label=version&color=3d7dca" alt="Latest release"></a>
 <img src="https://img.shields.io/badge/Cities%3A%20Skylines%20II-1.6.*-e8964a" alt="Game version">
 <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-8a67c0" alt="MIT license"></a>
</p>

A copy & paste tool for **Cities: Skylines II** - select props, trees, decals, painted surfaces and (since 1.1.0) whole buildings; copy them and stamp them anywhere with preserved layout, rotation, and height.

> **Save-game safe.** Copaste only ever writes the game's own (vanilla) components - it stores **no custom data in your save file**. A city touched by Copaste loads identically with or without the mod, and unsubscribing leaves nothing behind. Undo history and session data live in memory only.

## 📖 About this mod

Copaste started as a personal tool: we wanted a group copy/paste and blueprint workflow for prop detailing that no existing tool offered, so we built one for our own city. It grew feature by feature - undo, blueprints, a proper UI - until it felt useful enough to share, so here it is.

It was built by studying the source code of the excellent open-source mods credited below. Their MIT and Apache-2.0 licenses explicitly permit exactly this kind of learning and reuse - that openness is what makes the CS2 modding scene great, and this mod is published under the same spirit (MIT). Copaste is not a replacement for Move It or ctrlC; it is a different, detailing-focused workflow that can happily live alongside them.

## ✨ Features

The short version. The complete reference with every detail is in
[docs/features.md](docs/features.md).

- **Click & marquee selection** - click individual items, or drag a camera-aligned box on the ground to select whole groups at once (live highlight while dragging). Works on props, decals, trees, painted surfaces **and buildings**
- **Selection filters** - five toggle chips in the panel (Props, Trees, Decals, Surfaces, Buildings) control what selection picks up; right-click a chip to solo it (right-click again to bring all back). Buildings are off by default
- **Buildings** *(new in 1.1.0)* - move, rotate, raise/lower, copy, paste, blueprint and delete whole buildings **with everything they own**: driveways, pavements, purchased upgrades and attached props move as one piece. A **Relocate** mode walks the selected building to a new spot with live road snapping; **Road snap** also auto-faces pasted buildings to the nearest road. Buildings under construction can be deleted (with undo) but not moved
- **Building elements toggle** - lets selection reach elements owned by buildings, each following its filter chip: props, trees and decals (delete or rearrange them - deleted ones stay deleted for the whole session even when the building is moved or relocated) and **lot surfaces** (select and delete with undo - deleting a decoration surface also removes whatever it keeps spawning on the lot). With the toggle off, nothing building-owned can be selected, not even by click
- **Painted surfaces** - select by click or marquee, move with the selection, copy/paste, delete with undo, and save into blueprints
- **Copy & paste with preview** - the copied group follows your mouse as a ghost preview; click to place, as many times as you like
- **Layout preserved** - relative positions, rotations, and heights above terrain are kept exactly as in the original group
- **Group rotation** - hold right mouse button and drag to rotate a selection (or the paste preview) around its center
- **Move** - drag a selected prop to move the whole selection, layout and heights preserved
- **Delete** - remove the whole selection with one key. Combined with marquee + type filter it makes bulk cleanup trivial: box-select an area, press **T** on a tree to keep only that tree type, hit **Delete** - a whole overgrown forest gone in three clicks (Ctrl+Z brings it back, growth stages preserved)
- **Paste onto roads & paths** - the group lifts to the surface you point at
- **Original colors kept on paste** - pasted props keep the source prop's color variation *and* any custom color picked in the game's Customization tab; a blue bench stays blue. An Original/Random toggle in the panel restores the old randomized behavior. The ghost preview shows the real colors too
- **Align tools** - **Line** turns a messy selection into a tidy row (straight line, equal gaps, all props rotated the same way); **To prop** does the same anchored to a reference prop you click; **Circle** arranges the selection evenly on a circle. Afterwards, `[` and `]` (or the panel stepper) resize the layout live - the gap can also be typed in exact meters
- **Per-prop touch-ups** - **Alt+drag** moves a single prop out of the selection; **Alt+mouse wheel** spins every selected prop around its own axis (great after a Line align)
- **Ctrl+click picking** - selects props partially buried in other objects or buildings that a normal click can never reach; repeated Ctrl+clicks on the same spot cycle through everything piled up there
- **Anarchy-style pasting** - placement errors (overlapping items etc.) are ignored while pasting (toggleable in options); pasted props are protected from being hidden by the game when the [Anarchy](https://mods.paradoxplaza.com/mods/74604/Windows) mod is installed
- **Undo & Redo** - Ctrl+Z reverts the last action (move, rotate, align, height change, delete, paste, relocate), up to 32 steps back; Ctrl+Y re-applies it. Deleted trees come back with their original growth stage and colors; deleted buildings are rebuilt complete with pavement and driveways (fresh residents move in - simulation state is not copied)
- **Blueprints** - save the copied group to disk and reuse it in any city or save; managed from the in-game panel (paged list, inline rename); colors are stored too, older blueprint files still load
- **Type filter ("Same")** - press T to toggle a filter taken from the hovered/selected prop: marquee then only picks that exact type (works for trees too)
- **Nudge & snap** - fine-position the selection with Ctrl+arrows, drop it back to terrain with End
- **Toolbar button + panel** - a top-left toolbar button toggles the tool; the panel (draggable, remembers its position) shows counters, the selected prop's name, a per-prop list for selections up to 50 (hover rings the prop in the world, click isolates it), align controls and your blueprints
- **Rebindable hotkeys** - all shortcuts can be changed in Options → Copaste → Key bindings
- **English, German, French and Serbian** options localization

## 🎮 Controls (defaults)

The quick table. Every command with a full explanation, panel controls
included, is in [docs/commands.md](docs/commands.md).

| Action | Input |
|---|---|
| Toggle tool | **Ctrl+Shift+C** or the toolbar button |
| Select a prop | **Left click** (white circle on hover, blue when selected) |
| Pick a buried/overlapped prop | **Ctrl + left click** - repeat on the same spot to cycle through stacked props (Shift+Ctrl adds to selection) |
| Add/remove from selection | **Shift + left click** |
| Marquee selection | **Left click on empty ground + drag** (Shift adds to selection) |
| Move selection | **Left click on a prop + drag** - the whole selection follows the mouse |
| Move a single prop | **Alt + left click on a prop + drag** - only that prop moves |
| Clear selection | **Click on empty ground** |
| Copy selection | **Ctrl+C** |
| Paste (preview follows mouse) | **Ctrl+V**, then **left click** to place - repeat to stamp multiple copies |
| Rotate selection / paste preview | **Hold right mouse button + drag** (hold **Alt** to snap to 45°) |
| Spin each prop around its own axis | **Alt + mouse wheel** (15° per notch) |
| Align: tidy row | **Line** panel button - straight line, equal gaps, uniform rotation |
| Align: row through a reference prop | **To prop** panel button, then click the reference prop (RMB cancels) |
| Align: circle | **Circle** panel button (3+ props) |
| Adjust align gap live | **[** and **]** while an align button glows, or the panel stepper (type a number + Enter for exact meters) |
| Raise / lower selection or paste preview | **Page Up / Page Down** (hold for continuous) |
| Delete selection | **Delete** |
| Undo last action | **Ctrl+Z** (works in paste mode too - removes the last stamp) |
| Redo | **Ctrl+Y** |
| Relocate a building | **Tab** or the **Relocate** panel button (single building selected, Buildings filter on) - building follows the mouse with road snap; click places, **Tab**/right-click cancels |
| Type filter ("Same") | **T** - toggles a filter taken from the hovered/selected prop: while active, marquee only picks that prop type; clicking any prop switches the filter to its type; press again to clear |
| Match height | **Home** - arms height picking: the next prop you click sets the height for the whole selection |
| Nudge selection | **Ctrl + arrow keys** (camera-relative, hold for continuous) |
| Snap selection to ground | **End** (in paste mode: resets the height offset) |
| Save / load blueprints | **Panel buttons** - Save stores the selection/clipboard; clicking a name loads it and immediately starts pasting; ✎ renames, ✕ deletes |
| Back / clear / exit tool | **Quick right click** |

Blueprints are stored as text files in `...\Colossal Order\Cities Skylines II\ModsData\Copaste\Blueprints\` - rename or delete them freely. Props from missing mods are skipped on load.

Buildings are selectable since 1.1.0 behind the **Buildings** filter chip (off by default - with it off the tool behaves exactly like the prop-only versions). Vehicles and citizens are never selectable. By default selection only reaches free-standing objects; the **Building elements** toggle extends it to things owned by buildings - props, trees, decals and lot surfaces. Individual lot decorations that the game re-generates randomly on every lot update (clotheslines and similar) stay excluded on purpose - deleting them one by one could not stick without writing mod data into save files, which Copaste never does. Deleting the **decoration surface they spawn from** does stick, and that path is supported.

## 🛡️ Safety limits

To keep the game responsive, selection is capped at **1000 props** and selection circles are drawn for at most 400 at a time (everything selected is still highlighted). The tool guards its own update loop, so an unexpected error disables the current action instead of crashing the game.

## 🐛 Bug reports & feedback

Found a problem? [Open a bug report](https://github.com/viktorsx/Copaste/issues/new?template=bug_report.yml): the form asks for the few things needed to fix it, including the **Copaste.log** file (the form shows where it lives). Ideas go to a [feature request](https://github.com/viktorsx/Copaste/issues/new?template=feature_request.yml), questions to the [forum thread](https://forum.paradoxplaza.com/forum/threads/copaste-1-1-0-copy-paste-for-props-and-now-buildings.1938698/). Every report gets read.

## 📚 Developer documentation

Detailed docs for every part of the code live in [`docs/`](docs/README.md) - 
architecture, the tool system, the paste pipeline and color preservation, align
tools, undo, the blueprint file format, the cohtml UI (including Gameface
engine gotchas), settings/input, and build/deploy.

## 🌿 Branches

- **`main`** - matches the latest published release on Paradox Mods; releases are tagged `vX.Y.Z`
- **`dev`** - active development; merged into `main` at release time after in-game testing

## 🔧 Building from source

The project uses the [official CS2 modding toolchain](https://cs2.paradoxwikis.com/Modding_Toolchain) (install it via the game's launcher first - it sets up the `CSII_TOOLPATH` environment and `Mod.props`/`Mod.targets`):

```powershell
$env:DOTNET_ROLL_FORWARD = 'LatestMajor' # ModPostProcessor targets the .NET 6 runtime
dotnet build Copaste.csproj -c Release
```

The build runs the game's IL post-processor, produces Burst native libraries for Windows/Linux/macOS, and auto-deploys everything (DLL + UI files) to the game's local `Mods\Copaste` folder.

Two quirks worth knowing if you hack on this:
- `EntityManager.CreateEntity(archetype)` does not compile under the toolchain's `net48` profile (missing span types - the official project template has the same issue); use the `CreateEntity(archetype, count, allocator)` overload instead.
- The hand-written UI module (`ui/Copaste.mjs`) uses the game's runtime globals (`window.React`, `window["cs2/api"]`) instead of imports - ES imports silently break UI modules.

## 🤝 Transparency

AI-assisted development: the code was written with heavy use of AI tools (Claude Fable), directed, reviewed and hand-tested in-game by the author through many iterations. Bug reports are read and fixed by a human.

## ❤️ Credits & thanks

This mod stands on the shoulders of the CS2 modding community - it was written from scratch, but the patterns and APIs were learned from open-source mods whose licenses (MIT, Apache-2.0) explicitly welcome that. Sincere thanks to:

- **[yenyang](https://github.com/yenyang)** - the selection, highlighting, and raycasting patterns were learned from the MIT-licensed sources of *Better Bulldozer*, *Anarchy*, *Tree Controller*, and *Move It*; Anarchy's `PreventOverride` component is integrated at runtime when present
- **[algernon](https://github.com/algernon-A)** - the object placement approach (the game's definition pipeline) was learned from the Apache-2.0 sources of *Line Tool*
- **[Bruceyboy24804](https://github.com/Bruceyboy24804)** - *Node Controller*'s UI module served as the reference for how mod UI integrates with the game's interface
- **Colossal Order & Paradox** - for Cities: Skylines II and the [official modding documentation](https://cs2.paradoxwikis.com/Modding)
- The **Cities: Skylines Modding community** for keeping their mods open source - none of this would have been possible otherwise

### 🍵 Special thanks

A very special thank-you to **[Biffa (Biffa Plays Indie Games)](https://www.youtube.com/@BiffaPlaysCitiesSkylines)** and **[ConflictNerd](https://www.youtube.com/@ConflictNerd)** - simply for being the author's favorite Cities: Skylines YouTubers and the reason this game never gets boring. Keep the traffic flowing! 🍵

## 📄 License

MIT - see [LICENSE](LICENSE).
