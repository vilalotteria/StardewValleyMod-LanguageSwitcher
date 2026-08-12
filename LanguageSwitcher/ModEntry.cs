using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using StardewValley.Quests;

namespace LanguageSwitcher
{
    public class ModEntry : Mod
    {
        /// <summary>How many recent dialogue lines to keep for the replay log.</summary>
        private const int MaxDialogueLogEntries = 50;

        private ModConfig Config = null!;

        /// <summary>Recently-seen dialogue lines, oldest first, shown in the replay log (see <see cref="DialogueLogMenu"/>).</summary>
        private readonly List<DialogueLogEntry> DialogueLog = new();

        /// <summary>The <see cref="Dialogue"/> instance we last captured a line from, to detect when the player advances to a new line vs. starts a new conversation.</summary>
        private Dialogue? lastCapturedDialogue;

        /// <summary>The text we last captured from the current <see cref="Dialogue"/>. We dedupe by comparing the box's actual rendered string (<see cref="DialogueBox.getCurrentString"/>), not <see cref="Dialogue.currentDialogueIndex"/> - the index can briefly point at an intermediate/side-effect-only entry that's never actually shown to the player (e.g. still carrying an unstripped "$s" emotion token), so indexing off it captured lines the player never saw.</summary>
        private string? lastCapturedText;

        /// <summary>Temporary diagnostic-only tracker (separate from <see cref="lastCapturedText"/>) of every raw text change we see, even ones filtered out as duplicates - so we can see the full sequence of events instead of guessing at what happened.</summary>
        private string? lastPolledText;

        /// <summary><see cref="Dialogue.dialogues"/>.Count for the current <see cref="lastCapturedDialogue"/>, measured before any question was answered - lines at or beyond this index came from a player-response branch (see <see cref="TryGetPostChoiceTranslation"/>).</summary>
        private int originalDialogueLineCount = -1;

        /// <summary>The response options offered by the current question, if any, captured when it first appeared. Used both to log the options themselves and to work out which one the player picked afterward.</summary>
        private List<(string Key, string Text)>? pendingResponseOptions;

        /// <summary>The response key we've worked out the player actually chose for the current question, once resolved (see <see cref="TryGetPostChoiceTranslation"/>). Cached so we only need to resolve it once per question, not once per line.</summary>
        private string? resolvedPostChoiceKey;

        /// <summary>Whether the player pressed the hotkey while a dialogue-like menu was open, so the actual switch is deferred until it closes. See <see cref="OnUpdateTicked"/>.</summary>
        private bool switchPending;

        /// <summary>The language to return to when toggling away from <see cref="ModConfig.PreferredLanguage"/> - i.e. whatever language the game actually was in right before we last switched to the preferred one. See <see cref="ToggleLanguage"/> for why this can't just be a hardcoded "en".</summary>
        private LocalizedContentManager.LanguageCode? homeLanguageCode;

        /// <summary>Our own "Language: xx" HUD notification, tracked so the deferral in <see cref="OnUpdateTicked"/> doesn't treat it as a game notification worth waiting for.</summary>
        private HUDMessage? ownNotification;

        /// <summary>MonoGame SpriteFonts keyed by language, loaded lazily - see <see cref="GetOrLoadFontFor"/> for why this can't be a single shared font.</summary>
        private readonly Dictionary<LocalizedContentManager.LanguageCode, SpriteFont> fontsByLanguage = new();

        public override void Entry(IModHelper helper)
        {
            this.Config = helper.ReadConfig<ModConfig>();

            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.Display.RenderedActiveMenu += this.OnRenderedActiveMenu;
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        }

        /// <summary>Work out where to draw the title-screen button, positioned relative to the game's own About/Language icon stack (bottom-right) rather than a hardcoded spot - the top-left corner we originally used turned out to already have the game's music-mute button on it. Sits just above that icon stack, right-aligned with it.</summary>
        private static Rectangle GetTitleScreenButtonBounds(TitleMenu titleMenu)
        {
            const int width = 170;
            const int height = 36;
            const int gap = 8;

            if (titleMenu.languageButton != null)
            {
                Rectangle anchor = titleMenu.languageButton.bounds;
                return new Rectangle(anchor.Right - width, anchor.Y - height - gap, width, height);
            }

            // Fallback if the language icon isn't available for some reason - still bottom-right,
            // just not anchored to anything else on screen.
            return new Rectangle(Game1.uiViewport.Width - width - 24, Game1.uiViewport.Height - 260, width, height);
        }

        /// <summary>Whether the title screen is currently in the state where its own buttons (New/Load/About/Language) are shown, so ours should appear and disappear alongside them instead of hovering over the intro animation. Mirrors the conditions vanilla uses to draw aboutButton/languageButton in TitleMenu.draw.</summary>
        private static bool ShouldShowTitleScreenButton(TitleMenu titleMenu)
        {
            return TitleMenu.subMenu == null
                && titleMenu.titleInPosition
                && !titleMenu.isTransitioningButtons
                && titleMenu.HasActiveUser;
        }

        /// <summary>Draw a small button on the title screen that opens <see cref="LanguageConfigMenu"/>, so the player can set the target language and hotkeys without needing GMCM installed.</summary>
        private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
        {
            if (Game1.activeClickableMenu is not TitleMenu titleMenu || !ShouldShowTitleScreenButton(titleMenu))
                return;

            Rectangle bounds = GetTitleScreenButtonBounds(titleMenu);
            SpriteBatch b = e.SpriteBatch;
            IClickableMenu.drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White);

            SpriteFont font = this.GetOrLoadUiFont();
            const string label = "Language Switch";

            // Scale to fit the box rather than using a fixed scale - the font's actual glyph
            // widths vary by which language pack it was loaded for, so a hardcoded scale that
            // looks right for one language overflows for another.
            Vector2 rawSize = font.MeasureString(label);
            float scale = Math.Min(1f, (bounds.Width - 16) / Math.Max(1f, rawSize.X)) * 0.92f;
            Vector2 textPos = new(
                bounds.X + (bounds.Width - rawSize.X * scale) / 2,
                bounds.Y + (bounds.Height - rawSize.Y * scale) / 2);
            b.DrawString(font, label, textPos, Game1.textColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

            // Draw the cursor ourselves on top: this event fires after the game has already drawn
            // its cursor, so without this our button paints over it and the pointer looks like it's
            // behind/below the button while hovering it.
            b.Draw(
                Game1.mouseCursors,
                new Vector2(Game1.getMouseX(), Game1.getMouseY()),
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, Game1.options.gamepadControls ? 44 : 0, 16, 16),
                Color.White, 0f, Vector2.Zero, 4f + Game1.dialogueButtonScale / 150f, SpriteEffects.None, 1f);
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            // Forward raw key presses to the config menu while it's waiting to bind a new hotkey -
            // see LanguageConfigMenu.HandleKeyPress for why this needs the raw SButton rather than
            // going through the menu's normal receiveKeyPress.
            if (TitleMenu.subMenu is LanguageConfigMenu configMenu && configMenu.IsCapturingKey)
            {
                configMenu.HandleKeyPress(e.Button);
                return;
            }

            if (e.Button == SButton.MouseLeft && Game1.activeClickableMenu is TitleMenu titleMenu && ShouldShowTitleScreenButton(titleMenu))
            {
                Rectangle bounds = GetTitleScreenButtonBounds(titleMenu);
                Point mouse = Game1.getMousePosition();
                if (bounds.Contains(mouse))
                {
                    Game1.playSound("bigSelect");
                    TitleMenu.subMenu = new LanguageConfigMenu(this.Config, () => this.Helper.WriteConfig(this.Config), this.GetOrLoadUiFont(), this.GetOrLoadFontFor);
                }
            }
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            // Only allow switching once a save is loaded, to avoid misfires on the title screen.
            if (!Context.IsWorldReady)
                return;

            // Defer the switch while *any* menu is open, rather than listing specific menu types.
            // Every menu that has already resolved text onto the screen is exposed to the same
            // corruption (see the comment in the branch below), and enumerating them one at a time
            // just meant rediscovering the bug per menu: first DialogueBox (NPC conversation, TV),
            // then GameMenu (the world map is a GameMenu tab, not its own menu), then
            // LetterViewerMenu (mail). Deferring for anything open is both simpler and correct -
            // text already drawn was never going to retroactively translate anyway.
            //
            // Our own replay log is excluded because it handles the hotkey itself (see below) and
            // renders through mod-owned fonts that aren't affected by the game's current language.
            bool textSensitiveMenuOpen = Game1.activeClickableMenu is not null and not DialogueLogMenu;

            // Also wait out any on-screen HUD notification (e.g. the "!" new-quest popup after
            // reading mail). HUDMessage holds plain already-translated text with no key to
            // retranslate from, so switching under it turns it into tofu boxes. We could discard
            // such messages instead, but they're how the game tells the player something happened -
            // better to let them finish (a few seconds) and switch after, so nothing is missed and
            // nothing is unreadable. Our own switch notification is excluded, or it would block the
            // next switch for as long as it's visible.
            bool gameNotificationVisible = Game1.hudMessages.Any(message => !ReferenceEquals(message, this.ownNotification));

            bool deferSwitch = textSensitiveMenuOpen || gameNotificationVisible;

            if (this.Config.HotKey.JustPressed())
            {
                if (Game1.activeClickableMenu is DialogueLogMenu logMenu)
                {
                    // While the replay log is open, the hotkey only flips what this window shows
                    // (original vs. translated) - it doesn't touch the player's actual language,
                    // so browsing old conversations can't accidentally switch the live game.
                    logMenu.ShowTranslation = !logMenu.ShowTranslation;
                }
                else if (deferSwitch)
                {
                    // Text already on screen won't retroactively translate - and switching
                    // immediately would make it start rendering as garbage, because
                    // SpriteText.drawString picks its Latin-vs-bitmap-font path per character based
                    // on the *global* current language, not on what script the already-displayed
                    // text actually contains (confirmed via testing; there's no cache we can
                    // preserve to avoid this). We tried hiding the menu during the draw call to
                    // sidestep that, but temporarily nulling Game1.activeClickableMenu around the
                    // render pass froze the game - not safe. Instead, defer the whole switch: don't
                    // touch anything while the menu is open, so it keeps rendering exactly as it
                    // already was, and apply the switch the moment it closes (checked below), so a
                    // fresh interaction afterward uses the new language.
                    this.switchPending = true;
                }
                else
                {
                    this.ToggleLanguage();
                }
            }
            else if (this.switchPending && !deferSwitch)
            {
                this.switchPending = false;
                this.ToggleLanguage();
            }

            if (Game1.activeClickableMenu is DialogueBox { characterDialogue: not null } dialogueBox)
                this.CaptureDialogueLine(dialogueBox);

            if (this.Config.ReplayLogHotKey.JustPressed() && Game1.activeClickableMenu is null)
                Game1.activeClickableMenu = new DialogueLogMenu(this.DialogueLog, this.GetOrLoadFontFor, this.GetOrLoadUiFont(), this.Config.HotKey.ToString());
        }

        /// <summary>Get a SpriteFont able to render the given language, loading and caching one font per language on first use.</summary>
        /// <remarks>
        /// A single font isn't enough: the replay log shows entries in whatever language each line
        /// was captured in, and each language pack's SpriteFont only covers its own script plus
        /// Latin. Loading one font for the configured target language meant that if the target was
        /// e.g. Italian while the game ran in Chinese, every Chinese entry rendered as tofu boxes.
        /// So each entry picks the font matching its own language instead.
        /// <para>
        /// These font objects are owned entirely by this mod, unlike Game1.dialogueFont/smallFont
        /// (see the notes in <see cref="ToggleLanguage"/>), so they aren't affected by later
        /// language switches and stay valid for as long as a menu needs them.
        /// </para>
        /// </remarks>
        private SpriteFont GetOrLoadFontFor(LocalizedContentManager.LanguageCode? language)
        {
            LocalizedContentManager.LanguageCode key = language ?? LocalizedContentManager.LanguageCode.en;
            if (this.fontsByLanguage.TryGetValue(key, out SpriteFont? cached))
                return cached;

            LocalizedContentManager.LanguageCode original = LocalizedContentManager.CurrentLanguageCode;
            SpriteFont font;
            try
            {
                LocalizedContentManager.CurrentLanguageCode = key;
                font = this.Helper.GameContent.Load<SpriteFont>("Fonts\\SpriteFont1");
            }
            finally
            {
                LocalizedContentManager.CurrentLanguageCode = original;
            }

            this.fontsByLanguage[key] = font;
            return font;
        }

        /// <summary>Get the font for the mod's own UI chrome (button labels, headings). Uses the game's current display language, so that text sits in a font consistent with the rest of the UI the player is looking at.</summary>
        private SpriteFont GetOrLoadUiFont()
        {
            return this.GetOrLoadFontFor(LocalizedContentManager.CurrentLanguageCode);
        }

        /// <summary>Record the currently-displayed dialogue line (if we haven't already), with a best-effort translation into the configured preferred language, for the replay log.</summary>
        private void CaptureDialogueLine(DialogueBox dialogueBox)
        {
            Dialogue dialogue = dialogueBox.characterDialogue!;

            // A single conversation can show several lines through the same DialogueBox/Dialogue
            // instance as the player clicks through, so we track the last-seen text to avoid
            // logging the same line every tick, and reset when a new conversation starts. We
            // dedupe on the box's actual rendered text (not Dialogue.currentDialogueIndex) - the
            // index can briefly point at an intermediate/side-effect-only entry that's never
            // actually shown to the player, which previously got captured as a phantom line.
            if (!ReferenceEquals(dialogue, this.lastCapturedDialogue))
            {
                this.Monitor.Log(
                    $"[DialogueDiag] New Dialogue object: hash={dialogue.GetHashCode()}, key={dialogue.TranslationKey ?? "(none)"}, speaker={dialogue.speaker?.Name ?? "?"}, dialogues.Count={dialogue.dialogues.Count}",
                    LogLevel.Trace);
                this.lastCapturedDialogue = dialogue;
                this.lastCapturedText = null;
                this.lastPolledText = null;
                this.originalDialogueLineCount = dialogue.dialogues.Count;
                this.pendingResponseOptions = null;
                this.resolvedPostChoiceKey = null;
            }

            string text = dialogueBox.getCurrentString();
            if (text != this.lastPolledText)
            {
                this.Monitor.Log(
                    $"[DialogueDiag] raw text changed: index={dialogue.currentDialogueIndex}/{dialogue.dialogues.Count}, CurrentLanguageCode={LocalizedContentManager.CurrentLanguageCode}, text='{text}'",
                    LogLevel.Trace);
                this.lastPolledText = text;
            }

            if (!string.IsNullOrWhiteSpace(text) && text != this.lastCapturedText)
                this.CaptureLine(dialogue, speaker: dialogue.speaker?.Name ?? "?", text);

            // If the NPC is now actually showing a question (not just "this Dialogue happens to
            // have response data somewhere" - playerResponses gets populated for the whole entry
            // at parse time, long before the player scrolls to it, which is what caused the
            // options to previously get logged before the line that leads up to them), capture the
            // response options themselves too - the player might want to see (and translate) what
            // they could have said. These come straight from the base entry, not a player-choice-
            // driven sub-entry, so they're safe to translate the normal way.
            List<NPCDialogueResponse>? options = dialogue.getNPCResponseOptions();
            if (dialogue.isCurrentDialogueAQuestion() && options is { Count: > 0 } && this.pendingResponseOptions == null)
            {
                this.pendingResponseOptions = options.Select(o => (o.responseKey, o.responseText)).ToList();
                this.CaptureResponseOptions(dialogue, this.pendingResponseOptions);
            }
        }

        /// <summary>Record a captured dialogue line, with a best-effort translation, as a new replay-log entry.</summary>
        private void CaptureLine(Dialogue dialogue, string speaker, string text)
        {
            this.lastCapturedText = text;

            LocalizedContentManager.LanguageCode language = LocalizedContentManager.CurrentLanguageCode;

            string? translatedText = null;
            LocalizedContentManager.LanguageCode? translatedLanguage = null;
            if (this.TryParseLanguageCode(this.Config.PreferredLanguage, out LocalizedContentManager.LanguageCode preferred))
            {
                translatedLanguage = language == LocalizedContentManager.LanguageCode.en
                    ? preferred
                    : LocalizedContentManager.LanguageCode.en;

                // Once the player has answered a question, the game switches to a different, more
                // specific dialogue entry for the NPC's follow-up - but Dialogue.TranslationKey
                // stays frozen at the *original* key (see TryGetPostChoiceTranslation's remarks),
                // so re-parsing it directly like the normal path below would translate the wrong
                // content entirely.
                bool isPostChoice = this.pendingResponseOptions != null && dialogue.currentDialogueIndex >= this.originalDialogueLineCount;
                translatedText = isPostChoice
                    ? this.TryGetPostChoiceTranslation(dialogue, translatedLanguage.Value)
                    : this.TryGetOtherLanguageDialogueLine(dialogue, translatedLanguage.Value);
            }

            var entry = new DialogueLogEntry(speaker, dialogue.TranslationKey, CleanDialogueText(text)!, language, CleanDialogueText(translatedText), translatedLanguage);
            this.DialogueLog.Add(entry);
            if (this.DialogueLog.Count > MaxDialogueLogEntries)
                this.DialogueLog.RemoveAt(0);

            this.Monitor.Log($"[DialogueLog] {speaker} ({language}): {text}", LogLevel.Info);
            this.Monitor.Log(
                translatedText != null
                    ? $"[DialogueLog]   -> {translatedLanguage}: {translatedText}"
                    : $"[DialogueLog]   -> {translatedLanguage}: <no translation available for this line>",
                LogLevel.Info);
        }

        /// <summary>Log the response options offered by a question dialogue as their own entries, so the player can review (and see a translation of) choices they didn't pick. Translated by re-parsing the base entry in the other language and matching by position - safe here because these options aren't player-choice-dependent, unlike the NPC's follow-up once one is picked (see <see cref="TryGetPostChoiceTranslation"/>).</summary>
        private void CaptureResponseOptions(Dialogue dialogue, List<(string Key, string Text)> options)
        {
            string speaker = dialogue.speaker?.Name ?? "?";
            string? translationKey = dialogue.TranslationKey;
            LocalizedContentManager.LanguageCode language = LocalizedContentManager.CurrentLanguageCode;

            List<string>? translatedTexts = null;
            LocalizedContentManager.LanguageCode? translatedLanguage = null;
            if (this.TryParseLanguageCode(this.Config.PreferredLanguage, out LocalizedContentManager.LanguageCode preferred) && !string.IsNullOrEmpty(translationKey))
            {
                translatedLanguage = language == LocalizedContentManager.LanguageCode.en
                    ? preferred
                    : LocalizedContentManager.LanguageCode.en;

                LocalizedContentManager.LanguageCode original = LocalizedContentManager.CurrentLanguageCode;
                try
                {
                    LocalizedContentManager.CurrentLanguageCode = translatedLanguage.Value;
                    var parsed = new Dialogue(dialogue.speaker, translationKey);
                    List<NPCDialogueResponse>? parsedOptions = parsed.getNPCResponseOptions();
                    if (parsedOptions != null && parsedOptions.Count == options.Count)
                        translatedTexts = parsedOptions.Select(o => o.responseText).ToList();
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Couldn't translate response options for '{translationKey}': {ex.Message}", LogLevel.Trace);
                }
                finally
                {
                    LocalizedContentManager.CurrentLanguageCode = original;
                }
            }

            for (int i = 0; i < options.Count; i++)
            {
                string optionText = options[i].Text;
                string? translated = translatedTexts != null && i < translatedTexts.Count ? translatedTexts[i] : null;

                var entry = new DialogueLogEntry(speaker, null, CleanDialogueText(optionText)!, language, CleanDialogueText(translated), translatedLanguage, IsPlayerOption: true);
                this.DialogueLog.Add(entry);
                if (this.DialogueLog.Count > MaxDialogueLogEntries)
                    this.DialogueLog.RemoveAt(0);

                this.Monitor.Log($"[DialogueLog] {speaker} (option {i + 1}): {optionText}", LogLevel.Info);
            }
        }

        /// <summary>Get the translated text of a line that appeared after the player answered a dialogue question.</summary>
        /// <remarks>
        /// When the player picks a response, the game looks up the NPC's follow-up from a
        /// *different* dialogue entry (e.g. <c>Alex:Wed_01_02</c>) keyed by the chosen response's
        /// <see cref="NPCDialogueResponse.responseKey"/>, and appends its lines onto the same
        /// <see cref="Dialogue.dialogues"/> list - confirmed by reading <c>Dialogue.chooseResponse</c>'s
        /// source. But <see cref="Dialogue.TranslationKey"/> is a readonly field set once at
        /// construction, so it keeps pointing at the *original* entry (<c>Alex:Wed</c>) even after
        /// this switch - the game doesn't expose which follow-up key was actually used anywhere.
        /// <para>
        /// We recover it ourselves: for each response option that was offered (captured when the
        /// question appeared), load its raw text in the live language and see which one's first
        /// parsed line matches what's now actually showing - dialogue text is deterministic per
        /// key, so exactly one candidate should match. Once we know the real key, we can translate
        /// that specific entry directly instead of the stale original one.
        /// </para>
        /// </remarks>
        private string? TryGetPostChoiceTranslation(Dialogue dialogue, LocalizedContentManager.LanguageCode otherLanguage)
        {
            NPC? speaker = dialogue.speaker;
            string? loadedDialogueKey = speaker?.LoadedDialogueKey;
            if (speaker == null || loadedDialogueKey == null || this.pendingResponseOptions is not { Count: > 0 })
                return null;

            try
            {
                if (this.resolvedPostChoiceKey == null)
                {
                    if (this.originalDialogueLineCount < 0 || this.originalDialogueLineCount >= dialogue.dialogues.Count)
                        return null;

                    Dictionary<string, string> liveData = this.Helper.GameContent.Load<Dictionary<string, string>>(loadedDialogueKey);
                    string firstPostChoiceLine = dialogue.dialogues[this.originalDialogueLineCount].Text;

                    foreach ((string key, _) in this.pendingResponseOptions)
                    {
                        if (string.IsNullOrEmpty(key) || !liveData.TryGetValue(key, out string? rawText))
                            continue;

                        var candidate = new Dialogue(speaker, loadedDialogueKey + ":" + key, rawText);
                        if (candidate.dialogues.Count > 0 && candidate.dialogues[0].Text == firstPostChoiceLine)
                        {
                            this.resolvedPostChoiceKey = key;
                            this.Monitor.Log($"[DialogueDiag] resolved post-choice key: {loadedDialogueKey}:{key}", LogLevel.Trace);
                            break;
                        }
                    }
                }

                if (this.resolvedPostChoiceKey == null)
                    return null;

                LocalizedContentManager.LanguageCode original = LocalizedContentManager.CurrentLanguageCode;
                try
                {
                    LocalizedContentManager.CurrentLanguageCode = otherLanguage;
                    Dictionary<string, string> otherData = this.Helper.GameContent.Load<Dictionary<string, string>>(loadedDialogueKey);
                    if (!otherData.TryGetValue(this.resolvedPostChoiceKey, out string? otherRawText))
                        return null;

                    var parsed = new Dialogue(speaker, loadedDialogueKey + ":" + this.resolvedPostChoiceKey, otherRawText);
                    if (parsed.dialogues.Count == 0)
                        return null;

                    int relativeIndex = Math.Max(0, dialogue.currentDialogueIndex - this.originalDialogueLineCount);
                    int index = Math.Min(relativeIndex, parsed.dialogues.Count - 1);
                    return parsed.dialogues[index].Text;
                }
                finally
                {
                    LocalizedContentManager.CurrentLanguageCode = original;
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't resolve post-choice translation for {loadedDialogueKey}: {ex.Message}", LogLevel.Trace);
                return null;
            }
        }

        /// <summary>Strip leftover emotion-code tokens (<c>$h</c>/<c>$s</c>/<c>$u</c>/<c>$l</c>/<c>$a</c>) from captured text. The game normally strips these itself (see <c>Dialogue.checkEmotions</c>) when a line becomes the *current* one, but our capture sources - <c>DialogueBox.getCurrentString</c> for multi-page lines, and our own re-parsed <c>Dialogue</c> instances for translations - don't always go through that step, so a token can leak into what we display. This only touches our own copy of the text for the replay log; it doesn't modify the live Dialogue object, so it can't affect anything else the game does with it.</summary>
        private static string? CleanDialogueText(string? text)
        {
            if (text == null)
                return null;

            foreach (string token in new[] { "$h", "$s", "$u", "$l", "$a" })
                text = text.Replace(token, "");

            return text.Trim();
        }

        /// <summary>Get the text of the currently-displayed line of <paramref name="dialogue"/>, in a different language than the one currently active, without disturbing the player's actual current language.</summary>
        /// <remarks>
        /// <see cref="Dialogue.TranslationKey"/> points at the whole raw source entry (e.g.
        /// <c>Characters/Dialogue/Alex:Wed</c>), which can contain multiple pages/branches/conditions
        /// as one string - the game splits that into the individual lines in
        /// <see cref="Dialogue.dialogues"/> at parse time. A naive dictionary lookup of the
        /// translation key returns that whole raw blob, not the specific line being shown, so
        /// instead we re-parse the same raw entry in the other language through the game's own
        /// <see cref="Dialogue"/> constructor - confirmed by reading its source to be a pure
        /// text-parsing operation with no NPC/quest/global state involved - and pick the line at
        /// the same index.
        /// <para>
        /// Some entries contain "$r" (random) branches: which branch gets appended to
        /// <see cref="Dialogue.dialogues"/> is chosen randomly *at parse time*, independently each
        /// time the entry is parsed. Our re-parse above rolls its own independent pick, which is
        /// often a completely different, unrelated branch than the live conversation actually
        /// showed (confirmed via testing - a re-parsed "translation" turned out to be an unrelated
        /// line the player never saw). There's no way to reproduce the live roll from outside, so
        /// as a heuristic, if the reconstructed parse doesn't have the same number of lines as the
        /// live one, we treat the whole entry as unreliable and don't return a translation for it -
        /// showing nothing is better than confidently showing the wrong line for a language-learning
        /// tool. Lines that don't involve $r/$q branching (the common case) aren't affected.
        /// </para>
        /// </remarks>
        private string? TryGetOtherLanguageDialogueLine(Dialogue dialogue, LocalizedContentManager.LanguageCode otherLanguage)
        {
            string? translationKey = dialogue.TranslationKey;
            if (string.IsNullOrEmpty(translationKey))
                return null;

            LocalizedContentManager.LanguageCode original = LocalizedContentManager.CurrentLanguageCode;
            try
            {
                LocalizedContentManager.CurrentLanguageCode = otherLanguage;
                var parsed = new Dialogue(dialogue.speaker, translationKey);
                this.Monitor.Log(
                    $"[DialogueDiag] reparsed '{translationKey}' in {otherLanguage}: dialogues.Count={parsed.dialogues.Count} (live count was {dialogue.dialogues.Count})",
                    LogLevel.Trace);

                if (parsed.dialogues.Count == 0)
                    return null;
                if (parsed.dialogues.Count != dialogue.dialogues.Count)
                    return null; // likely diverged at a $r/$q branch - see remarks above

                int index = Math.Min(dialogue.currentDialogueIndex, parsed.dialogues.Count - 1);
                return parsed.dialogues[index].Text;
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't parse the {otherLanguage} version of translation key '{translationKey}': {ex.Message}", LogLevel.Trace);
                return null;
            }
            finally
            {
                LocalizedContentManager.CurrentLanguageCode = original;
            }
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            this.SetupGenericModConfigMenu();
        }

        private void ToggleLanguage()
        {
            if (!this.TryParseLanguageCode(this.Config.PreferredLanguage, out LocalizedContentManager.LanguageCode preferred))
            {
                this.Monitor.Log($"Invalid PreferredLanguage '{this.Config.PreferredLanguage}' in config.json; expected a language code like 'zh', 'ja', 'es', etc. Skipping switch.", LogLevel.Warn);
                return;
            }

            LocalizedContentManager.LanguageCode current = LocalizedContentManager.CurrentLanguageCode;
            LocalizedContentManager.LanguageCode target;
            if (current == preferred)
            {
                // Toggling away from the target language: go back to whatever language we were
                // actually in before switching to it, not a hardcoded "en". Without this, a player
                // whose game normally runs in e.g. Chinese and sets PreferredLanguage to Italian
                // would get stuck alternating between English and Italian after the first switch -
                // the toggle would never return to Chinese, since it always assumed "the other side"
                // was English.
                target = this.homeLanguageCode ?? LocalizedContentManager.LanguageCode.en;
            }
            else
            {
                this.homeLanguageCode = current;
                target = preferred;
            }

            // Callers are responsible for not invoking this while a dialogue-like menu is open -
            // see the deferral logic in OnUpdateTicked.
            LocalizedContentManager.CurrentLanguageCode = target;

            // The setter above only updates the current-language flag and fires OnLanguageChange;
            // it doesn't reload already-cached assets. Invalidating forces strings, dialogue, and
            // UI to reload in the new language on their next fresh interaction.
            //
            // We exclude Texture2D specifically: portraits, sprites, tilesheets, and the map
            // background aren't language-dependent, but invalidating them anyway forced an
            // expensive reload (visible as a multi-second stutter, worst when closing the world
            // map right after switching) the next time anything touched them, for no visual
            // benefit. Everything else (the Dictionary<string,string> "Strings" assets, dialogue,
            // Data/Objects, etc.) still gets invalidated normally.
            //
            // Note: this does NOT retroactively update text that's already been resolved into
            // an active dialogue/menu/queued message (e.g. mid-conversation, or a mailbox message
            // already queued when the hotkey is pressed) - closing and re-triggering the
            // interaction is required for those. We deliberately don't force GameLocation.reloadMap()
            // here to work around that: it also resets unrelated location state (water tiles,
            // seasonal tilesheets, seat calculations, etc.) and was observed to break in-progress
            // interactions like the mailbox message queue - not an acceptable tradeoff for this fix.
            this.Helper.GameContent.InvalidateCache(asset => asset.DataType != typeof(Texture2D));

            // SpriteText (the pixel-font renderer that draws NPC/TV dialogue box text - see
            // DialogueBox.draw, which calls only SpriteText.drawString, never Game1.dialogueFont
            // directly) only wires up its language-change handling and loads its glyph texture the
            // first time it's asked to draw non-Latin text - and that lazy load fires from inside
            // the draw loop, where loading textures isn't reliable. Load it proactively here
            // instead, same as the game's own Options menu does when the player picks a language.
            SpriteText.LoadFontData(target);

            // Game1 also caches several other language-dependent fields as plain static
            // fields set once at startup - notably dialogueFont/smallFont/tinyFont (regular
            // MonoGame SpriteFonts, a different system than SpriteText's bitmap font) and the
            // short day-of-week names used by the clock/calendar HUD. Neither InvalidateCache nor
            // the CurrentLanguageCode setter touch these, so without a refresh, text gets drawn
            // with the *old* language's SpriteFont - which doesn't contain glyphs for the new
            // language's characters, showing up as boxes (or, if MonoGame throws instead of
            // substituting a placeholder, the whole text draw silently fails - likely why the TV
            // menu's text disappeared entirely). Game1.TranslateFields() is the game's own method
            // for refreshing exactly this set of fields; it doesn't touch GameLocation/map state
            // or active dialogue, so it shouldn't reintroduce the mailbox-style regression we hit
            // from reloadMap() earlier.
            Game1.game1.TranslateFields();

            this.LogFontDiagnostics(target);
            this.RefreshDayTimeMoneyBox();
            this.ClearCachedItemDescriptions();
            this.ReloadQuestText();

            this.Monitor.Log($"Switched language to '{target}'.", LogLevel.Debug);

            if (this.Config.ShowNotifications)
            {
                string message = target == LocalizedContentManager.LanguageCode.en
                    ? "Language: English"
                    : $"Language: {target}";

                // Remember our own notification so the deferral in OnUpdateTicked can ignore it -
                // otherwise it would block the next switch for as long as it stays on screen.
                this.ownNotification = new HUDMessage(message, this.Config.NotificationDuration * 1000f);
                Game1.addHUDMessage(this.ownNotification);
            }
        }

        private bool TryParseLanguageCode(string value, out LocalizedContentManager.LanguageCode code)
        {
            return Enum.TryParse(value, ignoreCase: true, out code) && code != LocalizedContentManager.LanguageCode.mod;
        }

        /// <summary>Diagnostic logging (kept at Trace, not Info) covering the state of the two font systems (SpriteText's bitmap font and Game1's regular SpriteFonts) after a language switch. Not needed for normal operation since the underlying font/day-name bugs it was written to chase down are fixed, but left in - and easy to bump back up to Info - in case a similar rendering issue turns up again later.</summary>
        private void LogFontDiagnostics(LocalizedContentManager.LanguageCode target)
        {
            const char probeChar = '星'; // from "星期三" (Wednesday), which was seen rendering wrong

            this.Monitor.Log(
                $"[FontDiag] target={target} " +
                $"CurrentLanguageCode={LocalizedContentManager.CurrentLanguageCode} " +
                $"CurrentLanguageLatin={LocalizedContentManager.CurrentLanguageLatin}",
                LogLevel.Trace);

            this.Monitor.Log(
                $"[FontDiag] SpriteText: fontPages.Count={SpriteText.fontPages?.Count.ToString() ?? "null"}, " +
                $"fontPages[0]={(SpriteText.fontPages is { Count: > 0 } pages ? $"{pages[0].Width}x{pages[0].Height} disposed={pages[0].IsDisposed}" : "none")}",
                LogLevel.Trace);

            this.Monitor.Log(
                $"[FontDiag] Game1.smallFont: charCount={Game1.smallFont?.Characters.Count.ToString() ?? "null"}, containsProbeChar={Game1.smallFont?.Characters.Contains(probeChar).ToString() ?? "n/a"}",
                LogLevel.Trace);
            this.Monitor.Log(
                $"[FontDiag] Game1.dialogueFont: charCount={Game1.dialogueFont?.Characters.Count.ToString() ?? "null"}, containsProbeChar={Game1.dialogueFont?.Characters.Contains(probeChar).ToString() ?? "n/a"}",
                LogLevel.Trace);
            this.Monitor.Log(
                $"[FontDiag] Game1.tinyFont: charCount={Game1.tinyFont?.Characters.Count.ToString() ?? "null"}, containsProbeChar={Game1.tinyFont?.Characters.Contains(probeChar).ToString() ?? "n/a"}",
                LogLevel.Trace);

            this.Monitor.Log(
                $"[FontDiag] _shortDayDisplayName[2]='{Game1.shortDayDisplayNameFromDayOfSeason(3)}'",
                LogLevel.Trace);
        }

        /// <summary>The clock/date HUD widget caches its "day of week" display string on itself and only recomputes it when the in-game day actually changes - not when the language changes - so it keeps showing the old language's text until the next day. There's no public method to invalidate just that cache, so we replace the whole widget with a fresh instance (its constructor has no side effects beyond laying out button positions).</summary>
        private void RefreshDayTimeMoneyBox()
        {
            if (Game1.dayTimeMoneyBox is null)
                return;

            int index = Game1.onScreenMenus.IndexOf(Game1.dayTimeMoneyBox);
            if (index < 0)
                return;

            var fresh = new DayTimeMoneyBox();
            Game1.onScreenMenus[index] = fresh;
            Game1.dayTimeMoneyBox = fresh;
        }

        /// <summary>Clear the per-item cached description text so tooltips reload in the new language.</summary>
        /// <remarks>
        /// <see cref="Tool.description"/> lazily loads its text once and caches it on the item
        /// instance; nothing in the game ever clears that cache. So a tool the player has looked at
        /// keeps its original-language description forever, and after a switch it gets drawn with
        /// the *new* language's font - which usually can't render it, showing tofu boxes. Setting
        /// the property to null makes the getter reload it on next access.
        /// <para>
        /// This is most visible on the scythe, which is a <see cref="MeleeWeapon"/> the player
        /// carries as one long-lived instance. Note <see cref="Tool.DisplayName"/> has no such
        /// problem - it re-reads every access - which is why an affected tooltip shows a correctly
        /// translated name above a stale description.
        /// </para>
        /// </remarks>
        private void ClearCachedItemDescriptions()
        {
            try
            {
                // Covers player inventories, chests, items on tables, etc.
                Utility.ForEachItem(item =>
                {
                    if (item is Tool tool)
                        tool.description = null!;
                    return true;
                });
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't clear cached item descriptions: {ex.Message}", LogLevel.Trace);
            }
        }

        /// <summary>Make the player's active quests reload their title and description text, so the quest log shows them in the new language.</summary>
        /// <remarks>
        /// The three text properties on <see cref="Quest"/> cache inconsistently:
        /// <see cref="Quest.currentObjective"/> re-reads its source on every access, while
        /// <see cref="Quest.questTitle"/> and <see cref="Quest.questDescription"/> each load once
        /// behind a "_loaded" flag that nothing ever resets. That's why the quest log showed a mix
        /// of languages - the objective line tracked the current language while the title and
        /// description stayed frozen at whatever language they were first viewed in (and, once the
        /// font changed, rendered as tofu boxes).
        /// <para>
        /// Resetting those flags is the fix, and they're private/protected, so this uses SMAPI's
        /// reflection helper - the supported way to reach game internals. Each getter then re-runs
        /// the game's own loading logic on next access, which is important because that logic is
        /// non-trivial (it varies per quest subclass and falls back to the raw Data/Quests fields).
        /// An earlier attempt avoided reflection by blanking the text and calling the public
        /// reloadDescription()/reloadObjective() instead, but <see cref="Quest.reloadDescription"/>
        /// is empty on the base class - so for quest types that don't override it, that blanked the
        /// description entirely rather than reloading it.
        /// </para>
        /// </remarks>
        private void ReloadQuestText()
        {
            foreach (Quest quest in Game1.player.questLog)
            {
                if (quest == null)
                    continue;

                // required: false - if a game update renames these, we log and carry on rather
                // than throwing during a language switch.
                try
                {
                    this.Helper.Reflection.GetField<bool>(quest, "_loadedTitle", required: false)?.SetValue(false);
                    this.Helper.Reflection.GetField<bool>(quest, "_loadedDescription", required: false)?.SetValue(false);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Couldn't reset cached text on quest '{quest.id.Value}': {ex.Message}", LogLevel.Trace);
                }
            }
        }

        private void SetupGenericModConfigMenu()
        {
            IGenericModConfigMenuApi? gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (gmcm is null)
            {
                this.Monitor.Log("Generic Mod Config Menu not installed; skipping config menu registration. The mod still works via the hotkey and config.json.", LogLevel.Info);
                return;
            }

            gmcm.Register(
                mod: this.ModManifest,
                reset: () => this.Config = new ModConfig(),
                save: () => this.Helper.WriteConfig(this.Config)
            );

            gmcm.AddKeybindList(
                mod: this.ModManifest,
                getValue: () => this.Config.HotKey,
                setValue: value => this.Config.HotKey = value,
                name: () => "Toggle Language Hotkey"
            );

            gmcm.AddKeybindList(
                mod: this.ModManifest,
                getValue: () => this.Config.ReplayLogHotKey,
                setValue: value => this.Config.ReplayLogHotKey = value,
                name: () => "Dialogue Replay Log Hotkey"
            );

            gmcm.AddTextOption(
                mod: this.ModManifest,
                getValue: () => this.Config.PreferredLanguage,
                setValue: value => this.Config.PreferredLanguage = value,
                name: () => "Target Language",
                tooltip: () => "The language to toggle to from whatever language the game is currently in.",
                allowedValues: LanguageConfigMenu.SupportedLanguageCodes
            );

            gmcm.AddBoolOption(
                mod: this.ModManifest,
                getValue: () => this.Config.ShowNotifications,
                setValue: value => this.Config.ShowNotifications = value,
                name: () => "Show Notifications"
            );

            gmcm.AddNumberOption(
                mod: this.ModManifest,
                getValue: () => this.Config.NotificationDuration,
                setValue: value => this.Config.NotificationDuration = value,
                name: () => "Notification Duration (seconds)",
                min: 1,
                max: 10
            );
        }
    }
}
