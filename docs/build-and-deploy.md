# Build & Deploy

## Prerequisites

- Cities: Skylines II with the official **modding toolchain** installed
  (Options → Modding in-game installs it; it sets the `CSII_TOOLPATH`
  environment variable the csproj imports `Mod.props`/`Mod.targets` from).
- .NET SDK 8+. The toolchain's `ModPostProcessor` targets the .NET 6 runtime,
  so builds need roll-forward (below).

## Building

```powershell
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
dotnet build Copaste.csproj -c Release
```

A successful build automatically **deploys** to the local mods folder:

```
%LocalLow%\Colossal Order\Cities Skylines II\Mods\Copaste\
```

The game loads that folder as a local mod on next launch. The custom
`CopasteDeployUI` target in the csproj copies everything in `ui/` (mjs, css,
svg) next to the DLL — new UI assets need no csproj changes.

Deploy fails with "Access to the path ... is denied" while the game is
running — the game locks the DLL. Close the game first.

### Toolchain notes

- The project targets `net48` (the game's Unity Mono runtime).
- Span-dependent APIs are missing under this toolchain; e.g.
  `EntityManager.CreateEntity(archetype)` doesn't compile — use
  `CreateEntity(archetype, 1, Allocator.Temp)`. `System.Memory` is referenced
  compile-only (`ExcludeAssets=runtime`); the game provides its own copy.
- Game assemblies are referenced from the install's `Managed` folder with
  `Private=false` (never copied).

## Testing a development build

Disable the published Copaste in the game's mod manager (Paradox Mods) while
the local copy is present — running both versions at once loads the mod twice.

## Versioning & changelog

The version lives in two places that must move together:

- `Copaste.csproj` `<Version>` — shown in the panel header (read from the
  assembly at runtime)
- `Properties/PublishConfiguration.xml` `<ModVersion>` + `<ChangeLog>` — what
  Paradox Mods shows

`CHANGELOG.md` in the repo root mirrors the full history.

## Branches

- **`main`** — matches the latest published release; tagged `vX.Y.Z`.
- **`dev`** — active development; merged into `main` at release time after
  in-game testing.

## Publishing (maintainer only)

With the game closed:

```powershell
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
dotnet publish -c Release /p:PublishProfile=PublishNewVersion
```

This uploads a new version of the existing Paradox Mods entry (the mod id is
pinned in `PublishConfiguration.xml`). Every release is hand-tested in game
first — a fixed smoke-test checklist covering selection, copy/paste/undo,
blueprints, panel behavior and mod-coexistence.
