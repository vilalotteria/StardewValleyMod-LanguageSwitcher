using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.Quests;
using StardewValley.SpecialOrders;

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
                this.LogDiagnostic(
                    $"[DialogueDiag] New Dialogue object: hash={dialogue.GetHashCode()}, key={dialogue.TranslationKey ?? "(none)"}, speaker={dialogue.speaker?.Name ?? "?"}, dialogues.Count={dialogue.dialogues.Count}");
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
                this.LogDiagnostic(
                    $"[DialogueDiag] raw text changed: index={dialogue.currentDialogueIndex}/{dialogue.dialogues.Count}, CurrentLanguageCode={LocalizedContentManager.CurrentLanguageCode}, text='{text}'");
                this.lastPolledText = text;
            }

            // 只记录"确实完整显示给玩家看过"的行。仅靠文字变化不够：玩家点击推进时，
            // exitCurrentDialogue() 会先把 currentDialogueIndex++，于是在对话框真正关闭前的
            // 那一瞬间，getCurrentString() 已经返回下一行了——这就是之前"没说过的话出现在
            // 历史里"的成因（George 只说了第一句，第二句要再对话一次才会说，却被提前记下）。
            //
            // 两个条件来自 DialogueBox 自身的状态：
            //   transitioning              开场动画和 beginOutro() 关闭动画期间都为 true，
            //                              上面那个瞬间正处于 outro，因此会被排除
            //   characterIndexInDialogue   打字机进度；只有完整打完（或玩家点击跳过打字，
            //                              游戏会把它设到末尾）才算真的显示过
            bool fullyTyped = dialogueBox.characterIndexInDialogue >= text.Length - 1;
            bool actuallyShown = !dialogueBox.transitioning && fullyTyped;

            if (!string.IsNullOrWhiteSpace(text) && text != this.lastCapturedText && actuallyShown)
                this.CaptureLine(dialogue, speaker: dialogue.speaker?.Name ?? "?", text);

            // If the NPC is now actually showing a question (not just "this Dialogue happens to
            // have response data somewhere" - playerResponses gets populated for the whole entry
            // at parse time, long before the player scrolls to it, which is what caused the
            // options to previously get logged before the line that leads up to them), capture the
            // response options themselves too - the player might want to see (and translate) what
            // they could have said. These come straight from the base entry, not a player-choice-
            // driven sub-entry, so they're safe to translate the normal way.
            //
            // 这里同样要等 actuallyShown：isCurrentDialogueAQuestion() 在打字机还没打完时就已经
            // 为 true，而上面记录台词那一步被 actuallyShown 挡着，结果就是选项先落库、引出选项的
            // 那句台词几帧之后才补上——顺序反了。两处用同一个门槛，且台词的判断写在前面，顺序才
            // 稳定。（这个回归是加 actuallyShown 修"幽灵对话"时引入的。）
            List<NPCDialogueResponse>? options = dialogue.getNPCResponseOptions();
            if (actuallyShown && dialogue.isCurrentDialogueAQuestion() && options is { Count: > 0 } && this.pendingResponseOptions == null)
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
                translatedLanguage = this.GetCounterpartLanguage(language, preferred);

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

            // Trace，不是 Info：这几行是回放日志窗口做出来之前的 MVP 产物，那时只能靠控制台看
            // 结果。现在窗口就是给玩家看的地方，再往控制台刷一份纯属噪音——而且 SMAPI 日志在用户
            // 报问题时会被上传分享，没有理由把整段对话记录写进去。留着是因为排查捕获逻辑时还用得上。
            this.LogDiagnostic($"[DialogueLog] {speaker} ({language}): {text}");
            this.LogDiagnostic(
                translatedText != null
                    ? $"[DialogueLog]   -> {translatedLanguage}: {translatedText}"
                    : $"[DialogueLog]   -> {translatedLanguage}: <no translation available for this line>");
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
                translatedLanguage = this.GetCounterpartLanguage(language, preferred);

                LocalizedContentManager.LanguageCode original = LocalizedContentManager.CurrentLanguageCode;
                try
                {
                    LocalizedContentManager.CurrentLanguageCode = translatedLanguage.Value;
                    var parsed = new Dialogue(dialogue.speaker, translationKey);
                    List<NPCDialogueResponse>? parsedOptions = parsed.getNPCResponseOptions();
                    if (parsedOptions != null && parsedOptions.Count == options.Count)
                        translatedTexts = parsedOptions.Select(o => parsed.ReplacePlayerEnteredStrings(o.responseText)).ToList();
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

                this.LogDiagnostic($"[DialogueLog] {speaker} (option {i + 1}): {optionText}");
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
                            this.LogDiagnostic($"[DialogueDiag] resolved post-choice key: {loadedDialogueKey}:{key}");
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
                    return parsed.ReplacePlayerEnteredStrings(parsed.dialogues[index].Text);
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

        /// <summary>Matches a numeric portrait index token (<c>$9</c>, <c>$12</c>, ...). See <see cref="CleanDialogueText"/>.</summary>
        private static readonly Regex PortraitIndexToken = new(@"\$\d+", RegexOptions.Compiled);

        /// <summary>Strip the control tokens that shouldn't be shown to the player.</summary>
        /// <remarks>
        /// <para>
        /// This mirrors exactly what <c>Dialogue.checkForSpecialDialogueAttributes</c> (and the
        /// <c>Dialogue.checkEmotions</c> it calls) strips: the page-continuation marker <c>{</c>, the
        /// <c>%noturn</c> flag, the named emotion codes, and the numeric portrait-index form
        /// (<c>$9</c>, <c>$12</c>, ...).
        /// </para>
        /// <para>
        /// The game does that stripping only for the line that is *currently* being shown - every
        /// other entry in <c>Dialogue.dialogues</c> keeps its raw token. Our capture sources read
        /// those other entries directly (<c>DialogueBox.getCurrentString</c> for multi-page lines,
        /// and the re-parsed <c>Dialogue</c> instances we build for translations), so tokens leak
        /// into what we display - which is why each of these was found one at a time from a
        /// screenshot. If another stray symbol shows up in the log, this is the list to compare
        /// against that method.
        /// </para>
        /// <para>
        /// This only touches our own copy of the text for the replay log; it doesn't modify the live
        /// Dialogue object, so it can't affect anything else the game does with it.
        /// </para>
        /// </remarks>
        private static string? CleanDialogueText(string? text)
        {
            if (text == null)
                return null;

            foreach (string token in new[] { "{", "%noturn", "$h", "$s", "$u", "$l", "$a" })
                text = text.Replace(token, "");

            text = PortraitIndexToken.Replace(text, "");

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
            {
                // 没有 translation key 的台词各有各的来源，逐个试：文本内联在事件脚本里的剧情对话，
                // 以及文本内联在 Data\NPCGiftTastes 里的送礼反应。
                return this.TryGetEventDialogueLine(dialogue, otherLanguage)
                    ?? this.TryGetGiftReactionLine(dialogue, otherLanguage);
            }

            LocalizedContentManager.LanguageCode original = LocalizedContentManager.CurrentLanguageCode;
            try
            {
                LocalizedContentManager.CurrentLanguageCode = otherLanguage;
                var parsed = new Dialogue(dialogue.speaker, translationKey);
                this.LogDiagnostic(
                    $"[DialogueDiag] reparsed '{translationKey}' in {otherLanguage}: dialogues.Count={parsed.dialogues.Count} (live count was {dialogue.dialogues.Count})");

                if (parsed.dialogues.Count == 0)
                    return null;
                if (parsed.dialogues.Count != dialogue.dialogues.Count)
                    return null; // likely diverged at a $r/$q branch - see remarks above

                int index = Math.Min(dialogue.currentDialogueIndex, parsed.dialogues.Count - 1);

                // 游戏只在某一行成为"当前行"时才替换 @（玩家名）和 %spouse/%farm 等占位符，
                // 我们取的是别的下标，所以得自己走一遍它的替换逻辑
                return parsed.ReplacePlayerEnteredStrings(parsed.dialogues[index].Text);
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

        /// <summary>Try to find the other-language text for a line spoken during an event (a cutscene), which <see cref="TryGetOtherLanguageDialogueLine"/> can't handle.</summary>
        /// <remarks>
        /// <para>
        /// Event dialogue carries no <see cref="Dialogue.TranslationKey"/>: <c>Event.DefaultCommands.Speak</c>
        /// builds it as <c>new Dialogue(npc, null, text)</c> with the text taken straight out of the
        /// running event script. The script itself is the localised asset, so the way back to the
        /// other language is to reload the event's own data file and read the same command out of it.
        /// </para>
        /// <para>
        /// Lining the two scripts up is the delicate part, because a script can fork mid-event
        /// (<c>Event.ReplaceCurrentCommand</c> and the branch commands rewrite <c>eventCommands</c>
        /// in place), which would silently shift the indices apart. Three checks have to pass before
        /// we'll believe the match: the two scripts parse to the same number of commands, the command
        /// at the current index is a <c>speak</c> on both sides, and it's the same actor speaking.
        /// If any of them fails we return null and the log shows "no translation" - the same
        /// deliberate choice as the <c>$r</c> branch guard above, since a confidently-wrong line is
        /// worse than none in a tool people are using to learn.
        /// </para>
        /// </remarks>
        private string? TryGetEventDialogueLine(Dialogue dialogue, LocalizedContentManager.LanguageCode otherLanguage)
        {
            Event? currentEvent = Game1.CurrentEvent;
            if (currentEvent?.eventCommands == null || string.IsNullOrEmpty(currentEvent.fromAssetName))
                return null; // 生成式事件（婚礼等）没有源脚本可查

            string[] liveCommands = currentEvent.eventCommands;
            // 和游戏自己执行命令时一样夹取下标：Speak 在对话框还开着时会直接 return，
            // 不推进 CurrentCommand，所以这里指的就是当前这句台词
            int commandIndex = Math.Min(liveCommands.Length - 1, currentEvent.CurrentCommand);
            if (commandIndex < 0)
                return null;

            string[] liveArgs = ArgUtility.SplitBySpaceQuoteAware(liveCommands[commandIndex]);
            if (ArgUtility.Get(liveArgs, 0) != "speak")
                return null;

            string? actorName = ArgUtility.Get(liveArgs, 1);
            if (string.IsNullOrEmpty(actorName))
                return null;

            LocalizedContentManager.LanguageCode original = LocalizedContentManager.CurrentLanguageCode;
            try
            {
                LocalizedContentManager.CurrentLanguageCode = otherLanguage;

                Dictionary<string, string> otherEvents = this.Helper.GameContent.Load<Dictionary<string, string>>(currentEvent.fromAssetName);

                // 条目的 key 形如 "35/f Willy 0/t 600 1400"，第一段就是事件 id
                string? otherScript = otherEvents
                    .FirstOrDefault(pair => pair.Key.Split('/')[0] == currentEvent.id)
                    .Value;
                if (string.IsNullOrEmpty(otherScript))
                    return null;

                string[] otherCommands = Event.ParseCommands(otherScript);
                if (otherCommands.Length != liveCommands.Length)
                    return null; // 脚本中途分叉过，下标已经对不上了

                string[] otherArgs = ArgUtility.SplitBySpaceQuoteAware(otherCommands[commandIndex]);
                if (ArgUtility.Get(otherArgs, 0) != "speak" || ArgUtility.Get(otherArgs, 1) != actorName)
                    return null;

                string? otherText = ArgUtility.Get(otherArgs, 2);
                if (string.IsNullOrEmpty(otherText))
                    return null;

                // 一条 speak 里可能用 #$b# 分成好几页，游戏会把它们拆进 dialogues。
                // 交给 Dialogue 自己拆，再取和实况相同的那一页。
                var parsed = new Dialogue(dialogue.speaker, null, otherText);
                this.LogDiagnostic(
                    $"[DialogueDiag] reparsed event '{currentEvent.fromAssetName}' id={currentEvent.id} cmd={commandIndex} in {otherLanguage}: dialogues.Count={parsed.dialogues.Count} (live count was {dialogue.dialogues.Count})");

                if (parsed.dialogues.Count == 0 || parsed.dialogues.Count != dialogue.dialogues.Count)
                    return null;

                int index = Math.Min(dialogue.currentDialogueIndex, parsed.dialogues.Count - 1);
                return parsed.ReplacePlayerEnteredStrings(parsed.dialogues[index].Text);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't parse the {otherLanguage} version of event '{currentEvent.fromAssetName}' (id {currentEvent.id}): {ex.Message}", LogLevel.Trace);
                return null;
            }
            finally
            {
                LocalizedContentManager.CurrentLanguageCode = original;
            }
        }

        /// <summary>Try to find the other-language text for an NPC's reaction to being given a gift, which also carries no <see cref="Dialogue.TranslationKey"/>.</summary>
        /// <remarks>
        /// <para>
        /// <c>NPC.GetGiftReaction</c> builds these as <c>new Dialogue(this, null, ArgUtility.Get(rawFields, taste))</c>,
        /// where <c>rawFields</c> is the NPC's <c>Data\NPCGiftTastes</c> entry split on <c>/</c>. So
        /// the text lives at some field index of a localised asset, and the translation is the same
        /// index of that asset in the other language.
        /// </para>
        /// <para>
        /// We don't know the taste index (working it out again would need the gift item, which is
        /// long gone by the time we're logging the line), so we recover it by parsing each field of
        /// the *current*-language entry through the same constructor and seeing which one produces
        /// the line we're actually showing. That's more robust than reimplementing the taste rules,
        /// and it self-checks: if no field reproduces the line, we haven't understood where the text
        /// came from and return null rather than guess.
        /// </para>
        /// <para>
        /// Several fields can share the same source text - "......" is exactly the kind of reply a
        /// character like Sebastian gives to more than one gift tier - so a match isn't necessarily
        /// unique. That's only a problem if the candidates disagree once translated, so we compare
        /// the other-language values and accept the match when they all say the same thing.
        /// </para>
        /// </remarks>
        private string? TryGetGiftReactionLine(Dialogue dialogue, LocalizedContentManager.LanguageCode otherLanguage)
        {
            NPC? speaker = dialogue.speaker;
            if (speaker == null || !Game1.NPCGiftTastes.TryGetValue(speaker.Name, out string? liveRaw))
                return null;

            string[] liveFields = liveRaw.Split('/');
            int lineIndex = dialogue.currentDialogueIndex;
            string? liveText = CleanDialogueText(dialogue.dialogues[lineIndex].Text);
            if (string.IsNullOrEmpty(liveText))
                return null;

            // 哪些字段重新解析后能还原出正在显示的这一行？奇数下标存的是物品 ID 列表，
            // 不会匹配上，所以直接全扫一遍就行。
            List<int> candidates = new();
            for (int i = 0; i < liveFields.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(liveFields[i]))
                    continue;

                if (this.ParseFieldLine(speaker, liveFields[i], lineIndex, dialogue.dialogues.Count) == liveText)
                    candidates.Add(i);
            }

            if (candidates.Count == 0)
                return null; // 这行不是从 NPCGiftTastes 来的，或者我们理解错了它的来源

            LocalizedContentManager.LanguageCode original = LocalizedContentManager.CurrentLanguageCode;
            try
            {
                LocalizedContentManager.CurrentLanguageCode = otherLanguage;

                var otherTastes = this.Helper.GameContent.Load<Dictionary<string, string>>("Data\\NPCGiftTastes");
                if (!otherTastes.TryGetValue(speaker.Name, out string? otherRaw))
                    return null;

                string[] otherFields = otherRaw.Split('/');
                if (otherFields.Length != liveFields.Length)
                    return null;

                List<string> translations = candidates
                    .Select(i => this.ParseFieldLine(speaker, otherFields[i], lineIndex, dialogue.dialogues.Count))
                    .Where(text => !string.IsNullOrEmpty(text))
                    .Distinct()
                    .ToList()!;

                this.LogDiagnostic(
                    $"[DialogueDiag] gift reaction for {speaker.Name}: matched field(s) [{string.Join(", ", candidates)}], {translations.Count} distinct {otherLanguage} value(s)");

                // 多个字段共用同一句原文时，只有它们的译文也一致才敢用
                return translations.Count == 1 ? translations[0] : null;
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't parse the {otherLanguage} gift reactions for '{speaker.Name}': {ex.Message}", LogLevel.Trace);
                return null;
            }
            finally
            {
                LocalizedContentManager.CurrentLanguageCode = original;
            }
        }

        /// <summary>Parse one raw <c>Data\NPCGiftTastes</c> field through the same <see cref="Dialogue"/> constructor the game uses for it, and return the line at <paramref name="lineIndex"/> in the same cleaned-up form we display. Returns null if the field doesn't parse to a comparable shape.</summary>
        private string? ParseFieldLine(NPC speaker, string rawField, int lineIndex, int expectedLineCount)
        {
            try
            {
                var parsed = new Dialogue(speaker, null, rawField);
                if (parsed.dialogues.Count != expectedLineCount || lineIndex >= parsed.dialogues.Count)
                    return null;

                return CleanDialogueText(parsed.dialogues[lineIndex].Text);
            }
            catch
            {
                return null; // 物品 ID 列表之类的字段本来就不是对话，解析失败很正常
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
            // 切走之前先记下当前语言，这样之后切回来才知道该回到哪儿（见 GetCounterpartLanguage）。
            if (current != preferred)
                this.homeLanguageCode = current;

            LocalizedContentManager.LanguageCode target = this.GetCounterpartLanguage(current, preferred);

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
            this.ClearCachedItemText();
            this.ReloadQuestText();

            // Trace 而非 Debug：Debug 会打到控制台，而玩家每按一次快捷键就切一次，界面上本来就有
            // HUD 提示确认，控制台再来一行是多余的。Trace 仍然会写进 SMAPI 日志文件——用户报问题时
            // 分享的正是那个文件，所以"切换到底有没有执行"这个诊断价值一点没丢。
            this.Monitor.Log($"Switched language to '{target}'.", LogLevel.Trace);

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

        /// <summary>给定当前语言，算出切换的"另一端"是哪种语言。</summary>
        /// <remarks>
        /// 切换是在"游戏原本的语言"和 <see cref="ModConfig.PreferredLanguage"/> 之间进行的，
        /// 两端都可以是任意语言——英文并不特殊。所以另一端不能写死成 en：
        /// 已经在目标语言上时，回到 <see cref="homeLanguageCode"/>（即切过来之前所处的语言）；
        /// 否则就是目标语言本身。
        /// <para>
        /// 抽成一个方法是因为这个判断原先在三处各写了一遍。修 ToggleLanguage 时只改了那一处，
        /// 捕获对话的两处仍然写死 en，导致"中文游戏 + 意大利语目标"下回放日志把译文算成了英文。
        /// 现在只有这一个定义，不会再各自漂移。
        /// </para>
        /// </remarks>
        private LocalizedContentManager.LanguageCode GetCounterpartLanguage(
            LocalizedContentManager.LanguageCode current,
            LocalizedContentManager.LanguageCode preferred)
        {
            return current == preferred
                ? this.homeLanguageCode ?? LocalizedContentManager.LanguageCode.en
                : preferred;
        }

        /// <summary>Write one line of diagnostic output, unless <see cref="ModConfig.VerboseLogging"/> is off.</summary>
        /// <remarks>The dialogue and font diagnostics behind this are what every text-caching bug in this mod so far was actually diagnosed from, so they're worth keeping - but they fire several times per conversation, which buries everything else in the log during normal play. Gating them keeps both properties. Failures still log unconditionally; only this running commentary is optional.</remarks>
        private void LogDiagnostic(string message)
        {
            if (this.Config.VerboseLogging)
                this.Monitor.Log(message, LogLevel.Trace);
        }

        /// <summary>Diagnostic logging covering the state of the two font systems (SpriteText's bitmap font and Game1's regular SpriteFonts) after a language switch. Not needed for normal operation since the underlying font/day-name bugs it was written to chase down are fixed, but left in - behind <see cref="ModConfig.VerboseLogging"/> - in case a similar rendering issue turns up again later.</summary>
        private void LogFontDiagnostics(LocalizedContentManager.LanguageCode target)
        {
            if (!this.Config.VerboseLogging)
                return;

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
        /// <summary>Drop the per-item name/description text that some item classes cache on the instance itself, so it gets re-read in the new language the next time it's shown.</summary>
        /// <remarks>
        /// <para>
        /// Most items (<see cref="Object"/>, furniture, wallpaper) resolve their text from
        /// <c>ItemRegistry</c> on every access, so the content-cache invalidation in
        /// <c>ToggleLanguage</c> already covers them. The classes handled here don't: they resolve
        /// once and keep the result on the instance, where nothing invalidates it.
        /// </para>
        /// <para>
        /// Each one needs a different nudge, because each caches differently - see the comments
        /// inline. Getting this wrong is easy to miss, since a stale item only shows up when you
        /// happen to hover that particular kind of item after switching.
        /// </para>
        /// </remarks>
        private void ClearCachedItemText()
        {
            try
            {
                // 覆盖玩家背包、身上穿戴的装备、箱子、桌上的物品等
                // （ForEachItemHelper.ForEachItemInWorld 确实会遍历 shirtItem/pantsItem/boots/hat/rings）
                Utility.ForEachItem(item =>
                {
                    switch (item)
                    {
                        // Tool.DisplayName 每次读取都重新解析，只有描述缓存在 _description 里
                        case Tool tool:
                            tool.description = null!;
                            break;

                        // 这三类把名字和描述一起缓存成字段，只有字段为 null 时才会调
                        // loadDisplayFields() 重新读取
                        case Boots boots:
                            boots.displayName = null!;
                            boots.description = null!;
                            break;
                        case Hat hat:
                            hat.displayName = null!;
                            hat.description = null!;
                            break;
                        case Ring ring:
                            ring.displayName = null!;
                            ring.description = null!;
                            break;

                        // 衣服用 _loadedData 这个布尔量把关，字段本身不为 null，所以置空没用。
                        // 但也不能直接调 LoadData(forceReload: true)：那条路径会把 clothesColor
                        // 重置成白色，玩家染过的衣服会掉色。把标志位翻回 false，下次读 DisplayName
                        // 时游戏自己会走懒加载那条路——懒加载不带 forceReload，不碰颜色。
                        case Clothing clothing:
                            this.Helper.Reflection.GetField<bool>(clothing, "_loadedData").SetValue(false);
                            break;
                    }

                    return true;
                });
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't clear cached item text: {ex.Message}", LogLevel.Trace);
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
        /// <summary>Drop the cached title/description text on every quest the player can currently be looking at, so it gets rebuilt in the new language.</summary>
        /// <remarks>
        /// <para>
        /// Quests hold their resolved text on the instance behind a "have I loaded this yet" flag.
        /// Clearing the flag is enough: the next read re-runs the subclass's <c>reloadDescription</c>
        /// against the current language. (Calling that method directly doesn't work - it's empty on
        /// the <see cref="Quest"/> base class, so doing so blanked the descriptions instead.)
        /// </para>
        /// <para>
        /// The three places to look are easy to get wrong, because two of them aren't the quest log:
        /// the Help Wanted quest on the billboard is <see cref="Game1.questOfTheDay"/> and isn't in
        /// the log until it's accepted, and the special orders board reads from the team's own
        /// lists. Missing the billboard is what showed up as boxes (▯▯▯) - stale Chinese text drawn
        /// with the freshly-loaded Latin font.
        /// </para>
        /// </remarks>
        private void ReloadQuestText()
        {
            IEnumerable<Quest> quests = Game1.player.questLog;
            if (Game1.questOfTheDay != null)
                quests = quests.Append(Game1.questOfTheDay); // 公告板上的"招聘"任务，接受之前不在任务日志里

            foreach (Quest quest in quests)
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

            // 特殊订单是另一个类，缓存字段也不同名：为 null 时才重建，所以置空即可。
            // 它和"招聘"任务同属公告板的两个页签，问题完全一样。
            FarmerTeam team = Game1.player.team;
            foreach (SpecialOrder order in team.specialOrders.Concat(team.availableSpecialOrders))
            {
                if (order == null)
                    continue;

                try
                {
                    this.Helper.Reflection.GetField<string?>(order, "_localizedName", required: false)?.SetValue(null);
                    this.Helper.Reflection.GetField<string?>(order, "_localizedDescription", required: false)?.SetValue(null);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Couldn't reset cached text on special order '{order.questKey.Value}': {ex.Message}", LogLevel.Trace);
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

            // 这里刻意不注册 ShowNotifications 和 NotificationDuration。GMCM 和标题画面那个菜单
            // 应该提供同一套设置，而这两项不值得为它们在自己的菜单里做控件。字段仍然保留在
            // config.json 里并照常生效，需要的人可以直接改文件。
        }
    }
}
