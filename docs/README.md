# Copaste — Developer Documentation

Copaste is a Cities: Skylines II mod: a copy & paste tool for props, decals, trees,
painted surfaces and (since 1.1.0) buildings. This folder documents how the code
works, module by module, including the non-obvious constraints of the game's
modding surface that shaped the design.

## Documents

| Document | Covers |
|---|---|
| [features.md](features.md) | **User-facing reference of every feature** - selection, buildings, surfaces, editing, blueprints, panel, languages, safety |
| [commands.md](commands.md) | **Every command and control in tables** - mouse, keyboard defaults, panel controls, with explanations |
| [architecture.md](architecture.md) | Big picture: systems, lifecycle, data flow, safety principles |
| [tool-system.md](tool-system.md) | `CopasteToolSystem` select mode: raycast, selection, marquee, move drag, rotation, height, nudge, Ctrl+click cycle picking |
| [clipboard-and-paste.md](clipboard-and-paste.md) | Clipboard model, the definition pipeline, paste preview, post-paste fix-up, color/variation preservation |
| [align.md](align.md) | Align tools (Line, To prop, Circle), the live align session, gap controls |
| [buildings-and-surfaces.md](buildings-and-surfaces.md) | The Buildings toggle: copying buildings (construction trick) and painted surfaces (polygon pipeline) |
| [undo.md](undo.md) | Undo stack, snapshots, recreating deleted props |
| [blueprints.md](blueprints.md) | Blueprint file format (all versions), save/load, name sanitization |
| [ui.md](ui.md) | The cohtml (Gameface) panel: bindings, triggers, component structure, CSS constraints and gotchas |
| [settings-and-input.md](settings-and-input.md) | Mod settings, key bindings, localization |
| [build-and-deploy.md](build-and-deploy.md) | Building, local deploy, project layout |

## Source layout

```
src/
  Mod.cs               — mod entry point, logging, settings + localization registration
  CopasteSettings.cs   — ModSetting subclass: options, key bindings, hidden persisted state
  CopasteToolSystem.cs — the tool itself (selection, paste, align, undo, blueprints)
  CopasteToolSystem.Buildings.cs — buildings & painted surfaces partial (sub-tree
                         moves, relocate, road snap, lot transplant, sweeps)
  CopasteUISystem.cs   — UI bridge: value bindings and triggers between C# and the panel
  Localization.cs      — English, German, French and Serbian dictionaries for the Options screen
ui/
  Copaste.mjs          — the in-game UI module (toolbar button + panel), plain JS, no build step
  Copaste.css          — panel styles (game "rem" units)
  *.svg                — icons (all must declare width/height, see ui.md)
```

## Reading order

If you are new to CS2 modding, read `architecture.md` first — it explains the ECS
systems the mod plugs into and the rules the game imposes. Then follow whatever
module you care about. Each document ends with a **Gotchas** section listing the
mistakes we already made so you don't repeat them.
