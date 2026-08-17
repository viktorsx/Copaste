# Architecture

## Overview

Copaste is built from four cooperating pieces:

```
┌─────────────────┐   value bindings / triggers   ┌──────────────────┐
│  Copaste.mjs    │ ◄───────────────────────────► │ CopasteUISystem  │
│  (cohtml panel) │                               │ (UISystemBase)   │
└─────────────────┘                               └────────┬─────────┘
                                                           │ direct calls
                                                  ┌────────▼─────────┐
                                                  │ CopasteToolSystem │
                                                  │ (ToolBaseSystem)  │
                                                  └────────┬─────────┘
                                                           │ ECS + definition pipeline
                                                  ┌────────▼─────────┐
                                                  │   Game systems    │
                                                  └───────────────────┘
```

- **`Mod.cs`** implements `IMod`. `OnLoad` creates the settings, registers key
  bindings, adds the EN/DE/FR/SR localization dictionaries, and locates the mod's
  install directory to register the `coui://copaste/` UI host for icons.
- **`CopasteToolSystem`** derives from `Game.Tools.ToolBaseSystem`. It is the whole
  tool: selection, marquee, move/rotate/height editing, align tools, paste,
  undo and blueprint IO live here.
- **`CopasteUISystem`** derives from `Game.UI.UISystemBase`. It owns every
  `ValueBinding` (C# → UI state) and `TriggerBinding` (UI → C# action) and is the
  only place UI names are bound to tool methods.
- **`ui/Copaste.mjs`** is a game UI module. The game loads it, calls the default
  export with a `moduleRegistry`, and the module appends a toolbar button to
  `GameTopLeft` and the panel to `Game`.

## Tool lifecycle

The game has exactly one active tool at a time (`ToolSystem.activeTool`).
Activating Copaste (Ctrl+Shift+C or the toolbar button) sets
`toolSystem.activeTool = copasteToolSystem`; the game then calls
`OnStartRunning`, per-frame `OnUpdate(JobHandle)`, and `OnStopRunning` when
another tool takes over.

`OnUpdate` is wrapped in try/catch: any exception logs the error and calls
`ResetToolState()` instead of letting the game's tool loop crash. This is a core
safety rule — a bug in Copaste must degrade to "tool reset", never to a CTD.

The tool has three modes (`Mode.Select` / `Mode.Paste` / `Mode.Relocate`)
with separate update paths: `UpdateSelectMode()`, `UpdatePasteMode()` and
`UpdateRelocateMode()` (see
[buildings-and-surfaces.md](buildings-and-surfaces.md)).

## How props are placed (the definition pipeline)

Copaste never constructs placed objects by hand during paste. It follows the
game's official flow, the same one the object placement tool uses:

1. For each clipboard item, create a *definition entity* on the
   `ToolOutputBarrier` command buffer carrying `CreationDefinition` (which prefab,
   which random seed) + `ObjectDefinition` (position, rotation, elevation, age…)
   + `Updated`.
2. The game's generation systems turn definitions into **`Temp` preview
   entities** (the ghost you see under the mouse).
3. When the user clicks, the tool sets `applyMode = ApplyMode.Apply` and the game
   converts the Temp entities into real, permanent objects.

Because the game assigns final entity ids itself, Copaste re-discovers what it
just pasted in a short post-paste scan (see
[clipboard-and-paste.md](clipboard-and-paste.md#post-paste-fix-up)) so undo can
target exactly those entities.

## What the tool edits directly

For operations on props that already exist (move, rotate, height, nudge, align),
the tool writes `Game.Objects.Transform` (and `Game.Objects.Elevation`)
directly via `EntityManager.SetComponentData`, then adds `Updated` and
`BatchesUpdated` so the simulation and renderer pick the change up. This is the
pattern used by established editing mods and does not touch save-format data.

## Safety principles

Every feature must pass all three:

1. **No custom data in save files.** Copaste only writes vanilla components the
   game already serializes. Uninstalling the mod leaves the city 100% clean.
2. **Official pipelines only.** Placement goes through the definition pipeline;
   no Harmony patches anywhere in the mod.
3. **Errors kill the action, never the game.** Try/catch around the update loop,
   hard limits (1000 selected props, 400 overlay circles), and graceful fallback
   when optional integrations (Anarchy) are missing.

## Optional Anarchy integration

Copaste implements "anarchy pasting" itself via the official
`ToolSystem.ignoreErrors` flag. If the Anarchy mod happens to be installed, its
`PreventOverride` component type is resolved by reflection at runtime and added
to pasted props so the game doesn't hide them as overlapping; when Anarchy is
absent everything still works, minus that extra protection.

## Threading model

Everything runs on the main thread inside `OnUpdate`. The heavy queries
(`m_PropQuery.ToEntityArray` etc.) are synchronous snapshots taken only when
needed (marquee scan steps, click picking, post-paste frames) — the mod
schedules no jobs of its own.
