# Language Switcher

*[中文说明](README.zh-CN.md)*

**[Download on Nexus Mods](https://www.nexusmods.com/stardewvalley/mods/50762)** · [Changelog](CHANGELOG.md) · [Buy me a boba tea 🧋](https://ko-fi.com/vilalotteria)

Stardew Valley locks its language at startup — to see how something is worded in another language
you'd normally have to quit, change the setting, and reload your save.

This mod switches the language instantly, with one key, while you keep playing. It's built for people
using the game to learn a language: flip to your target language to see how a menu, item or
conversation is phrased, then flip straight back to check you understood it.

It works between **any two** of the game's 12 languages, not just English and one other. If you play
in Chinese and are learning Italian, that's Chinese to Italian — English never has to be involved.

## Features

**Switch language with one key** (default `K`)
Menus, item names, tooltips and dialogue update immediately — no restart, no reloading your save.
Switches between whatever language you're currently playing in and your chosen target.

**Dialogue replay log** (default `L`)
A scrollable window of recent NPC dialogue, with a key to flip each line between the original and a
translation. This exists because the game fixes text into a language the moment it's drawn, so a
conversation already on screen can't be re-translated (see [Known limitations](#known-limitations))
— the log is how you go back and read it afterwards. Player dialogue choices are captured too, so
you can see the options you didn't pick.

**Set up from the title screen**
Pick your target language and rebind both hotkeys from a **Language Switch** button on the title
screen. Keys already used by the game (WASD, arrows, E, F, M, Y, Tab, number keys…) are rejected, so
you can't accidentally break your controls.

**Generic Mod Config Menu supported** (optional)
If you have [GMCM][gmcm], the same settings are available from its in-game Mod Options page.

Languages: English, Русский, 中文, Deutsch, Português, Français, Español, 日本語, 한국어, Italiano,
Türkçe, Magyar

## Requirements

- [SMAPI](https://smapi.io/) 4.5.0 or later
- Stardew Valley 1.6+
- [Generic Mod Config Menu][gmcm] — optional; the mod works fully without it, you just lose that one
  extra way to change settings

## Installation

1. Install SMAPI, if you haven't already
2. Download the mod from [Nexus Mods](https://www.nexusmods.com/stardewvalley/mods/50762)
3. Unzip it into `Stardew Valley/Mods/`, so you end up with
   `Mods/LanguageSwitcher/manifest.json`
4. Launch the game through SMAPI
5. On the title screen, click **Language Switch** (bottom right) to pick your target language

## Usage

- Press **K** in-game to switch language; a notification appears in the corner
- Press **L** to open the dialogue replay log. Pressing K *inside* that window only toggles between
  original and translated text there — it doesn't change the game's language
- Use the **Language Switch** button on the title screen to pick a target language or rebind keys

### Settings

| Field | Default | Description | Where to change it |
|---|---|---|---|
| `PreferredLanguage` | `zh` | Target language: `en`/`ru`/`zh`/`de`/`pt`/`fr`/`es`/`ja`/`ko`/`it`/`tr`/`hu` (the same set the game itself offers) | Title screen, GMCM, or `config.json` |
| `HotKey` | `K` | Key that switches language | Title screen, GMCM, or `config.json` |
| `ReplayLogHotKey` | `L` | Key that opens the dialogue replay log | Title screen, GMCM, or `config.json` |
| `ShowNotifications` | `true` | Whether to show a HUD message when switching | `config.json` |
| `NotificationDuration` | `3` | How many seconds that message stays up | `config.json` |
| `VerboseLogging` | `false` | Whether to write dialogue-capture, translation-lookup and font diagnostics to the SMAPI log. Turn it on when a line isn't being captured or translated — the log is the useful thing to attach to a bug report. Errors and warnings are logged either way | `config.json` |

`config.json` is generated in the mod folder on first run.

The hotkey fields accept several bindings and modifier combos (`K, LeftShift + K`) when edited in
`config.json` or GMCM. The title-screen menu captures a single key, so rebinding there replaces a
multi-key binding with one key — it warns you before doing so.

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

## Building from source

You'll need the [.NET 6 SDK](https://dotnet.microsoft.com/download). The game ships the .NET 6
runtime, so the target framework has to stay `net6.0` — it can't be moved to a newer version.

```bash
dotnet build LanguageSwitcher/LanguageSwitcher.csproj
```

[Mod Build Config](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-package.md)
does two extra things for you: it deploys the build to `Stardew Valley/Mods/LanguageSwitcher/` (launch
SMAPI and it's there), and it writes a release-ready zip to `LanguageSwitcher/bin/<config>/net6.0/`.

The game holds the DLL open while it's running, so deploying fails with `being used by another
process`. Close the game first, or build without deploying:

```bash
dotnet build LanguageSwitcher/LanguageSwitcher.csproj -p:EnableModDeploy=false
```

For a release package, build in Release — the zip lands at
`LanguageSwitcher/bin/Release/net6.0/LanguageSwitcher <version>.zip`:

```bash
dotnet build LanguageSwitcher/LanguageSwitcher.csproj -c Release
```

When investigating a problem, set `VerboseLogging` to `true` in `config.json` to get the dialogue
capture and translation lookups written to the SMAPI log.

## License

[MIT](LICENSE)

[gmcm]: https://www.nexusmods.com/stardewvalley/mods/5098
