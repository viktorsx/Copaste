# Settings & Input

## CopasteSettings

`CopasteSettings : ModSetting`, stored at `ModsSettings/Copaste/Copaste`. The
Options screen is split into two tabs via `SettingsUITabOrder` /
`SettingsUISection`, with key bindings grouped by purpose
(`SettingsUIGroupOrder` + `SettingsUIShowGroupName`):

- **General** → Behavior: *Mod language* (Auto / English / German / French /
  Serbian — Auto follows the game) and *Ignore placement errors when pasting*
  (default on; paste stamp only, no override protection)
- **General** → Panel: *Panel theme* (Copaste / Vanilla — Vanilla hosts the
  panel in the game's own chrome), *Panel size* (80–125%) and *Text size*
  (90–130%, scales lettering only)
- **General** → Limits: *Selection limit* (500–5000, default 1000),
  *Selection outline limit* (100–1000, default 400) and *Selected props list
  limit* (10–100, default 50). One limit is fixed, not a slider: a single
  Delete handles at most 500 objects (`kMaxDeleteAtOnce`); larger selections
  are refused with the error sound, because a one-frame removal of that size
  has crashed the game in testing. A batched delete that lifts it is planned
- **Key bindings** → Tool / Clipboard / Editing / Nudge / Align groups

Hidden persisted state (`[SettingsUIHidden]`, not shown in Options):

| Property | Purpose |
|---|---|
| `PanelX`, `PanelY` | saved panel position in px; `-1` = "use CSS default" |
| `RandomPasteVariation` | the Original/Random paste-look toggle |
| `SelectProps/Trees/Decals/Surfaces` | selection filter chips (default on) |
| `SelectBuildings` | Buildings filter chip (default **off**) |
| `SelectFences` | Fences filter chip (default **off**) |
| `SelectNetworks` | Networks filter chip (default **off**) |
| `RoadSnapPaste` | road snap toggle for paste/relocate (default on) |
| `SelectBuildingProps` | marquee may grab building-owned props (default off) |

`SetDefaults()` must reset every property — the game calls it for "Reset
settings".

## Key bindings

All bindings are `ProxyBinding` properties with `SettingsUIKeyboardBinding`
defaults, resolved once in `OnCreate` via `Mod.Settings.GetAction(name)` and
enabled/disabled in `OnStartRunning`/`OnStopRunning` so they only fire while
the tool is active:

| Action | Default |
|---|---|
| Toggle tool | Ctrl+Shift+C |
| Copy / Paste | Ctrl+C / Ctrl+V |
| Undo / Redo | Ctrl+Z / Ctrl+Y |
| Relocate building | Tab (starts relocate when exactly one finished building is selected; cancels while relocating) |
| Delete selection | Delete |
| Raise / Lower | PgUp / PgDn |
| Select same (filter) | T |
| Snap to ground / Match height | End / Home |
| Nudge away/towards/left/right | Ctrl+Arrows |
| Align gap + / − | `]` / `[` |

Users can rebind everything in Options → Copaste → Key bindings. Note: the
game caches binding state; after changing *defaults* in code, an existing
installation may need "Reset key bindings".

Some gestures intentionally bypass the binding system and read
`Keyboard.current` / `Mouse.current` raw, because they are modifier
combinations on the mouse: Shift+click (additive select), Ctrl+click (cycle
pick), Alt+drag (move single prop), Alt+wheel (spin props), Alt during RMB
rotation (45° snap), Alt during a node drag (slide along the neighbor
line) and U (underground view). Raw reads are always guarded by
`m_UiTyping` and, for clicks, by tool-raycast validity.

**Tap Alt** (straighten selected network nodes) is raw too — the game's
binding system cannot bind a bare modifier as a key (`BindingKeyboard` has
no Alt entry). Since Alt is also a modifier for the gestures above, the
tap is edge-triggered: pressing Alt arms it, any mouse button or wheel
movement while held disarms it, and releasing Alt clean fires it.

## Localization

`Localization.cs` builds one dictionary per locale (English `en-US`, German
`de-DE`, French `fr-FR`, Serbian `sr-SP`) and `Mod.OnLoad` registers them as
localization sources (non-English ones only when the game/I18N mod reports
the locale as supported). Every
settings option, tab, group and binding name needs entries via the
`ModSetting` locale-id helpers (`GetOptionLabelLocaleID`,
`GetOptionDescLocaleID`, `GetOptionTabLocaleID`, `GetOptionGroupLocaleID`,
`GetBindingKeyLocaleID`, `GetBindingMapLocaleID`).

**Mod language override**: the `ModLanguage` option adds the chosen
dictionary as an extra source for the *active* game locale and reloads it,
so the mod can speak its own language while the game keeps another. The
apply routine is re-entrancy guarded: `AddSource`/`RemoveSource`/
`ReloadActiveLocale` all fire `onActiveDictionaryChanged` — which is also
the hook that re-applies the override on a game-language change — and
without the guard that recursion crashed the game.

Panel strings (`ui/Copaste.mjs`) go through the game's `cs2/l10n` module:
every label, tooltip and hint calls `t("KEY", "English fallback")`, and the
`COPASTE.*` keys live in the same per-locale dictionaries. Without the
module (or a missing key) the panel silently stays English.

When renaming or removing a feature, grep the localization file for the old
name — stale descriptions ("Spaced" after the button was renamed) are easy to
miss because nothing breaks at compile time.
