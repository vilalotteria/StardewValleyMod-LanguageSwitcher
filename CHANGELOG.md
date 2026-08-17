# Changelog

## 1.0.3

Four fixes, all in the same family: text that the game resolves once and then keeps, where nothing
tells it to reconsider after a language switch.

- **Clothing now follows the language switch.** Shirts, pants, boots, hats and rings kept the name
  and description they were first shown with. Regular items were never affected, since they re-read
  their text from the item registry every time.
- **Event dialogue can be translated in the replay log.** Lines spoken during cutscenes carry no
  translation key — the text is inline in the event script — so they previously showed "no
  translation available". The mod now reads the same line back out of the event's own data file in
  the other language.
- **No more stray symbols in the replay log.** Leftover control tokens (`{`, `%noturn`, `$9`) could
  end up in a displayed line, and the player's name (`@`) and `%spouse`-style placeholders weren't
  filled in on translated lines.
- **The replay log stays scrolled to the bottom** when you toggle between original and translation.
  Switching changes how tall the entries are, which previously left a gap below the last line or
  pushed it out of view.

Also: dialogue and font diagnostics are no longer written to the SMAPI log during normal play. Set
`VerboseLogging` to `true` in `config.json` to turn them back on when investigating a problem.
Errors and warnings are logged either way.

## 1.0.2

- The replay log no longer writes captured dialogue to the SMAPI console during normal play.

## 1.0.1

- Fixed player dialogue choices being recorded in the replay log before the line that prompts them.

## 1.0.0

Initial release.

- Switch between any two of the game's 12 languages with a hotkey, without restarting or reloading
  your save.
- Bilingual dialogue replay log, including the player response options you didn't pick.
- Title-screen configuration for the target language and both hotkeys, with no dependency on Generic
  Mod Config Menu.
- Optional Generic Mod Config Menu support.
