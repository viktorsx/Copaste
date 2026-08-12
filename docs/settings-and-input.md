# Settings & Input

## CopasteSettings

`CopasteSettings : ModSetting`, stored at `ModsSettings/Copaste/Copaste`. The
Options screen is split into two tabs via `SettingsUITabOrder` /
`SettingsUISection`, with key bindings grouped by purpose
(`SettingsUIGroupOrder` + `SettingsUIShowGroupName`):

- **General** → Behavior: *Anarchy while pasting* (default on)
- **Key bindings** → Tool / Clipboard / Editing / Nudge / Align groups

Hidden persisted state (`[SettingsUIHidden]`, not shown in Options):

| Property | Purpose |
|---|---|
| `PanelX`, `PanelY` | saved panel position in px; `-1` = "use CSS default" |
| `RandomPasteVariation` | the Original/Random paste-look toggle |

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
| Copy / Paste / Undo | Ctrl+C / Ctrl+V / Ctrl+Z |
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
rotation (45° snap). Raw reads are always guarded by `m_UiTyping` and, for
clicks, by tool-raycast validity.

## Localization

`Localization.cs` builds one dictionary per locale (English `en-US`, Serbian
`sr-SP`) and `Mod.OnLoad` registers them as localization sources. Every
settings option, tab, group and binding name needs entries via the
`ModSetting` locale-id helpers (`GetOptionLabelLocaleID`,
`GetOptionDescLocaleID`, `GetOptionTabLocaleID`, `GetOptionGroupLocaleID`,
`GetBindingKeyLocaleID`, `GetBindingMapLocaleID`).

Panel strings (`ui/Copaste.mjs`) are currently English-only by design — the
panel is compact and its labels are near-universal (Copy, Paste, Undo…).

When renaming or removing a feature, grep the localization file for the old
name — stale descriptions ("Spaced" after the button was renamed) are easy to
miss because nothing breaks at compile time.
