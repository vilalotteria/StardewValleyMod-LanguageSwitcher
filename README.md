# Language Switcher

*[中文说明](README.zh-CN.md)*

A Stardew Valley SMAPI mod that instantly switches between your current game language and a target
language of your choice, with a hotkey. Built for people learning a language through the game — flip
to your target language to see how menus, items and dialogue are worded, then flip back to compare.

## Features

- **Toggle language with one key** (default `K`) — switches between the game's current language and
  your chosen target. Menus, item names, dialogue and so on update immediately, no restart needed.
  Both ends are freely chosen, so Chinese ↔ Italian or English ↔ Japanese work just as well.
- **Dialogue replay log** (default `L`) — a scrollable window of recent NPC dialogue in
  chronological order, with a key to flip each entry between its original text and a translation.
  Because the game locks text into a language the moment it's displayed, conversations already on
  screen can't be retranslated (see [Known limitations](#known-limitations)) — this window is how
  you review them afterward.
- **Title-screen setup** — a **Language Switch** button in the bottom-right of the title screen lets
  you pick the target language, rebind both hotkeys, and reset them to defaults, with no extra mods
  required. Rebinding rejects keys the game already uses (WASD, arrows, E, F, M, Y, Tab, number
  keys, and so on).
- **GMCM support** (optional) — if [Generic Mod Config Menu][gmcm] is installed, every setting is
  also editable from its in-game Mod Options page.

## Requirements

- [SMAPI](https://smapi.io/) 4.5.0 or later
- Stardew Valley 1.6+
- [Generic Mod Config Menu][gmcm] — optional; the mod works fully without it, you just lose that one
  extra way to change settings

## Installation

1. Install SMAPI, if you haven't already
2. Drop the `LanguageSwitcher` folder into `Stardew Valley/Mods/`
3. Launch the game through SMAPI

## Usage

- Press **K** in-game to switch language; a notification appears in the corner
- Press **L** to open the dialogue replay log. Pressing K *inside* that window only toggles between
  original and translated text there — it doesn't change the game's language
- Use the **Language Switch** button on the title screen to pick a target language or rebind keys

### Settings

Editable in `config.json` (generated on first run), in the GMCM menu, or in the title-screen
Language Switch popup:

| Field | Default | Description |
|---|---|---|
| `HotKey` | `K` | Key that switches language |
| `ReplayLogHotKey` | `L` | Key that opens the dialogue replay log |
| `PreferredLanguage` | `zh` | Target language: `en`/`ru`/`zh`/`de`/`pt`/`fr`/`es`/`ja`/`ko`/`it`/`tr`/`hu` (same set the game itself offers) |
| `ShowNotifications` | `true` | Whether to show a HUD message when switching |
| `NotificationDuration` | `3` | How many seconds that message stays up |

## Known limitations

These come from how the game handles text: it resolves text into a specific language at the moment
it's displayed, and can't change it retroactively.

- **Switching waits for the current screen to finish.** If a menu is open (NPC dialogue, TV, a
  letter, the map, your inventory…) or a notification is on screen when you press the key, the
  switch is held until they close. This keeps everything readable, at the cost of a short delay.
- **Dialogue already shown stays in its original language.** Use the dialogue replay log (`L`) to
  see it side by side.
- **Translations for branching dialogue are best-effort.** For dialogue involving random branches or
  player choices, the translation depends on being able to identify which branch actually ran. When
  that can't be determined, the entry shows "no translation" rather than risk showing the wrong line.

## License

[MIT](LICENSE)

[gmcm]: https://www.nexusmods.com/stardewvalley/mods/5098
