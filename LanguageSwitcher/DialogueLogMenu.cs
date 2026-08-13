using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace LanguageSwitcher
{
    /// <summary>Shows recently-captured NPC dialogue lines, in either their original language or a best-effort translation - toggled locally with <see cref="ShowTranslation"/>, independent of the player's actual current language. Uses a plain MonoGame SpriteFont (not SpriteText) so it isn't affected by the game's current language - see the notes in ModEntry.ToggleLanguage for why SpriteText can't safely do that.</summary>
    internal sealed class DialogueLogMenu : IClickableMenu
    {
        private const int SidePadding = 32;
        private const int TopPadding = 56; // extra room so the first line doesn't sit under the box's decorative top border
        private const int BottomPadding = 32;
        private const int FooterHeight = 36; // reserved for the "showing original/translation" hint, so it doesn't sit under the bottom border either
        private const int LineGap = 4;
        private const int EntryGap = 20;
        private const int ScrollSpeedPixels = 60;
        private const int ScrollbarReservedWidth = 24; // width reserved on the right of the text area for the scrollbar track

        private readonly IReadOnlyList<DialogueLogEntry> entries;

        /// <summary>Resolves a font able to render a given language. Each entry needs its own font: a language pack's SpriteFont only covers its own script plus Latin, so e.g. an Italian font can't draw Chinese entries (they came out as tofu boxes when this menu used one shared font).</summary>
        private readonly Func<LocalizedContentManager.LanguageCode?, SpriteFont> fontResolver;

        /// <summary>Font for this menu's own chrome (the footer hint), which is always ASCII.</summary>
        private readonly SpriteFont uiFont;

        private readonly string hotkeyLabel;

        private int scrollPixels;

        /// <summary>Whether to show each entry's translated text instead of the original. Toggled by ModEntry when the player presses the language hotkey while this menu is open - this only changes what's displayed here, it doesn't touch the player's actual current language.</summary>
        public bool ShowTranslation { get; set; }

        private const int MenuWidth = 900;
        private const int MenuHeight = 640;

        public DialogueLogMenu(IReadOnlyList<DialogueLogEntry> entries, Func<LocalizedContentManager.LanguageCode?, SpriteFont> fontResolver, SpriteFont uiFont, string hotkeyLabel)
            : base(
                (Game1.uiViewport.Width - MenuWidth) / 2,
                (Game1.uiViewport.Height - MenuHeight) / 2,
                MenuWidth,
                MenuHeight,
                showUpperRightCloseButton: true)
        {
            this.entries = entries;
            this.fontResolver = fontResolver;
            this.uiFont = uiFont;
            this.hotkeyLabel = hotkeyLabel;

            // Start scrolled to the bottom (the most recent conversation), matching how a chat log
            // is usually read - scroll up from there to see older history.
            int viewHeight = this.height - TopPadding - BottomPadding - FooterHeight;
            int contentHeight = this.MeasureContentHeight(this.width - SidePadding * 2 - ScrollbarReservedWidth);
            this.scrollPixels = Math.Max(0, contentHeight - viewHeight);
        }

        /// <summary>Re-centre the menu when the window is resized (e.g. toggling fullscreen). The base implementation scales the old position proportionally, which doesn't preserve centring - it left the menu hanging off the corner of the screen after switching back from fullscreen.</summary>
        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);

            this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
            this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;

            // The close button's position was baked in relative to the old bounds.
            this.initializeUpperRightCloseButton();
        }

        public override void receiveScrollWheelAction(int direction)
        {
            int viewHeight = this.height - TopPadding - BottomPadding - FooterHeight;
            int contentHeight = this.MeasureContentHeight(this.width - SidePadding * 2 - ScrollbarReservedWidth);
            int maxScroll = Math.Max(0, contentHeight - viewHeight);
            this.scrollPixels = Math.Clamp(this.scrollPixels - Math.Sign(direction) * ScrollSpeedPixels, 0, maxScroll);
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.6f);
            drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);

            int viewTop = this.yPositionOnScreen + TopPadding;
            int viewBottom = this.yPositionOnScreen + this.height - BottomPadding - FooterHeight;
            int viewLeft = this.xPositionOnScreen + SidePadding;
            int viewWidth = this.width - SidePadding * 2 - ScrollbarReservedWidth;

            if (this.entries.Count == 0)
            {
                b.DrawString(this.uiFont, "(No dialogue captured yet - go talk to someone!)", new Vector2(viewLeft, viewTop), Game1.textColor);
            }
            else
            {
                // Chronological order (oldest at top), like a chat log.
                int y = viewTop - this.scrollPixels;
                foreach ((bool isOptionsGroup, List<DialogueLogEntry> group) in BuildRenderUnits(this.entries))
                {
                    y = isOptionsGroup
                        ? this.DrawOptionsGroup(b, group, viewLeft, y, viewWidth, viewTop, viewBottom)
                        : this.DrawEntry(b, group[0], viewLeft, y, viewWidth, viewTop, viewBottom);
                    y += EntryGap;
                }
            }

            this.DrawScrollbar(b, viewLeft + viewWidth + 12, viewTop, viewBottom, viewWidth);

            string hint = this.ShowTranslation
                ? $"Showing translation - press {this.hotkeyLabel} to show original"
                : $"Showing original - press {this.hotkeyLabel} to show translation";
            b.DrawString(this.uiFont, hint, new Vector2(this.xPositionOnScreen + SidePadding, viewBottom + 8), Color.Gray);

            base.draw(b);
            this.drawMouse(b);
        }

        /// <summary>Draw a vertical scrollbar track + thumb so it's clear how far up/down the log the player currently is, and roughly how much more there is to scroll.</summary>
        private void DrawScrollbar(SpriteBatch b, int x, int viewTop, int viewBottom, int contentWidth)
        {
            int viewHeight = viewBottom - viewTop;
            int contentHeight = this.MeasureContentHeight(contentWidth);
            if (contentHeight <= viewHeight)
                return; // everything fits, no need for a scrollbar

            const int trackWidth = 6;
            b.Draw(Game1.staminaRect, new Rectangle(x, viewTop, trackWidth, viewHeight), Color.SaddleBrown * 0.25f);

            int maxScroll = contentHeight - viewHeight;
            int thumbHeight = Math.Max(24, viewHeight * viewHeight / contentHeight);
            int thumbY = viewTop + (viewHeight - thumbHeight) * this.scrollPixels / Math.Max(1, maxScroll);
            b.Draw(Game1.staminaRect, new Rectangle(x, thumbY, trackWidth, thumbHeight), Color.SaddleBrown);
        }

        /// <summary>Compute the total height of all entries without drawing anything, used to size the scrollbar range.</summary>
        private int MeasureContentHeight(int maxWidth)
        {
            int y = 0;
            foreach ((bool isOptionsGroup, List<DialogueLogEntry> group) in BuildRenderUnits(this.entries))
            {
                y = isOptionsGroup
                    ? this.DrawOptionsGroup(null, group, 0, y, maxWidth, int.MinValue, int.MaxValue)
                    : this.DrawEntry(null, group[0], 0, y, maxWidth, int.MinValue, int.MaxValue);
                y += EntryGap;
            }
            return y;
        }

        /// <summary>Group consecutive player-response-option entries (see <see cref="DialogueLogEntry.IsPlayerOption"/>) together, so they're rendered as one "[Options]" block instead of repeating a header per option. Everything else stays its own group of one.</summary>
        private static List<(bool IsOptionsGroup, List<DialogueLogEntry> Entries)> BuildRenderUnits(IReadOnlyList<DialogueLogEntry> entries)
        {
            var units = new List<(bool, List<DialogueLogEntry>)>();
            List<DialogueLogEntry>? currentOptionsRun = null;

            foreach (DialogueLogEntry entry in entries)
            {
                if (entry.IsPlayerOption)
                {
                    currentOptionsRun ??= new List<DialogueLogEntry>();
                    currentOptionsRun.Add(entry);
                    continue;
                }

                if (currentOptionsRun != null)
                {
                    units.Add((true, currentOptionsRun));
                    currentOptionsRun = null;
                }
                units.Add((false, new List<DialogueLogEntry> { entry }));
            }
            if (currentOptionsRun != null)
                units.Add((true, currentOptionsRun));

            return units;
        }

        private int DrawEntry(SpriteBatch? b, DialogueLogEntry entry, int x, int y, int maxWidth, int viewTop, int viewBottom)
        {
            string speaker = entry.Speaker;
            LocalizedContentManager.LanguageCode? language = this.ShowTranslation ? entry.TranslatedLanguage : entry.Language;
            string? text = this.ShowTranslation ? entry.TranslatedText : entry.Text;
            SpriteFont font = this.fontResolver(language);

            // 每条记录的译文是在捕获那一刻按当时的目标语言算好并存下来的，之后改目标语言不会
            // 回溯重算。如果当时的目标语言恰好就等于游戏语言（比如英文游戏 + 目标设成 en），
            // "译文"和原文就是同一种语言、同一段文字——两边看起来一模一样，很像是切换没生效。
            // 这里明确说出来，而不是把同一段文字再画一遍。
            bool sameAsOriginal = this.ShowTranslation
                && entry.TranslatedLanguage.HasValue
                && entry.TranslatedLanguage == entry.Language;

            y = this.DrawWrapped(b, font, $"{speaker} ({(language.HasValue ? language.Value.ToString() : "?")}):", x, y, maxWidth, Color.SaddleBrown, viewTop, viewBottom);

            if (sameAsOriginal)
            {
                return this.DrawWrapped(
                    b, font,
                    $"(captured while the target language was also {entry.Language} - same as the original)",
                    x + 24, y, maxWidth - 24, Color.Gray, viewTop, viewBottom);
            }

            y = this.DrawWrapped(
                b, font,
                text ?? "(no translation available for this line)",
                x + 24, y, maxWidth - 24,
                text != null ? Color.Black : Color.Gray,
                viewTop, viewBottom);

            return y;
        }

        /// <summary>Draw a batch of player response options as one visually distinct block: a single "[Options]" header, each option indented and prefixed with "&gt;", in muted gray tones (rather than the brown/black used for what NPCs actually said) with a thin accent bar down the left edge, since these are choices the player *could* make, not dialogue that was said.</summary>
        private int DrawOptionsGroup(SpriteBatch? b, List<DialogueLogEntry> options, int x, int y, int maxWidth, int viewTop, int viewBottom)
        {
            const int indent = 28;
            int startY = y;

            int measuredY = this.DrawWrapped(null, this.uiFont, "[Options]", x + indent, y, maxWidth - indent, Color.Gray, viewTop, viewBottom);
            foreach (DialogueLogEntry entry in options)
            {
                string? text = this.ShowTranslation ? entry.TranslatedText : entry.Text;
                SpriteFont optionFont = this.fontResolver(this.ShowTranslation ? entry.TranslatedLanguage : entry.Language);
                measuredY = this.DrawWrapped(
                    null, optionFont,
                    "  >  " + (text ?? "(no translation available for this line)"),
                    x + indent, measuredY, maxWidth - indent,
                    Color.DimGray,
                    viewTop, viewBottom);
            }

            if (b != null && measuredY + this.uiFont.LineSpacing >= viewTop && startY <= viewBottom)
                b.Draw(Game1.staminaRect, new Rectangle(x + 4, startY, 4, measuredY - startY - LineGap), Color.RosyBrown);

            int drawY = this.DrawWrapped(b, this.uiFont, "[Options]", x + indent, y, maxWidth - indent, Color.Gray, viewTop, viewBottom);
            foreach (DialogueLogEntry entry in options)
            {
                string? text = this.ShowTranslation ? entry.TranslatedText : entry.Text;
                SpriteFont optionFont = this.fontResolver(this.ShowTranslation ? entry.TranslatedLanguage : entry.Language);
                drawY = this.DrawWrapped(
                    b, optionFont,
                    "  >  " + (text ?? "(no translation available for this line)"),
                    x + indent, drawY, maxWidth - indent,
                    Color.DimGray,
                    viewTop, viewBottom);
            }

            return drawY;
        }

        private int DrawWrapped(SpriteBatch? b, SpriteFont font, string text, int x, int y, int maxWidth, Color color, int viewTop, int viewBottom)
        {
            int lineHeight = font.LineSpacing + LineGap;
            foreach (string line in WrapText(font, text, maxWidth))
            {
                // Require the whole line to fit, not just its top edge - drawing on "top edge in
                // view" let the last line spill past the content area and collide with the footer
                // hint below it.
                if (b != null && y >= viewTop && y + lineHeight <= viewBottom)
                    b.DrawString(font, line, new Vector2(x, y), color);
                y += lineHeight;
            }
            return y;
        }

        /// <summary>Break text into lines that fit within <paramref name="maxWidth"/>. Wraps at spaces for Latin text and between individual characters for CJK/Hangul/fullwidth text, which don't use spaces between words.</summary>
        private static List<string> WrapText(SpriteFont font, string text, int maxWidth)
        {
            var lines = new List<string>();
            var line = new StringBuilder();
            var pendingWord = new StringBuilder();

            void AppendPiece(string piece)
            {
                string candidate = line + piece;
                if (line.Length > 0 && font.MeasureString(candidate).X > maxWidth)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                    line.Append(piece.TrimStart());
                }
                else
                {
                    line.Append(piece);
                }
            }

            void FlushWord()
            {
                if (pendingWord.Length == 0)
                    return;
                AppendPiece(pendingWord.ToString());
                pendingWord.Clear();
            }

            foreach (char c in text)
            {
                if (c == ' ')
                {
                    FlushWord();
                    AppendPiece(" ");
                }
                else if (IsWideCharacter(c))
                {
                    FlushWord();
                    AppendPiece(c.ToString());
                }
                else
                {
                    pendingWord.Append(c);
                }
            }
            FlushWord();

            if (line.Length > 0)
                lines.Add(line.ToString().TrimEnd());

            return lines;
        }

        /// <summary>Rough check for CJK/Hangul/fullwidth ranges, good enough to allow wrapping between individual characters instead of only at spaces.</summary>
        private static bool IsWideCharacter(char c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF)
                || (c >= 0x3040 && c <= 0x30FF)
                || (c >= 0xAC00 && c <= 0xD7A3)
                || (c >= 0xFF00 && c <= 0xFFEF);
        }
    }
}
