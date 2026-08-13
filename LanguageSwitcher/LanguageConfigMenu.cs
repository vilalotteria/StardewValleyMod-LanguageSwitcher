using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;

namespace LanguageSwitcher
{
    /// <summary>A title-screen popup (shown via <see cref="TitleMenu.subMenu"/>) for picking the mod's target language and rebinding its hotkeys, without needing GMCM. Uses plain MonoGame SpriteFonts owned by the mod, same as <see cref="DialogueLogMenu"/>, so it isn't affected by the game's current language. Content scrolls (like DialogueLogMenu) rather than trying to guess a fixed size that fits everything - that repeatedly didn't, across several attempts.</summary>
    internal sealed class LanguageConfigMenu : IClickableMenu
    {
        /// <summary>The languages the player can pick as a target, in their own native names.</summary>
        /// <remarks>
        /// This deliberately matches the set the game's own LanguageSelectionMenu offers, rather
        /// than every value in <see cref="LocalizedContentManager.LanguageCode"/> - the enum also
        /// contains <c>th</c> (Thai), but no vanilla menu exposes it, so offering it here would let
        /// the player pick a language the game doesn't actually ship a selectable translation for.
        /// (<c>mod</c> is likewise excluded: it's a marker for custom mod-added languages, not a
        /// language itself.)
        /// <para>
        /// English is included: the toggle switches between the game's current language and this
        /// target, so English is a valid target like any other - it was only missing while an
        /// earlier version of the toggle hardcoded English as the other side.
        /// </para>
        /// </remarks>
        private static readonly (string Code, LocalizedContentManager.LanguageCode Language, string Label)[] Languages =
        {
            ("en", LocalizedContentManager.LanguageCode.en, "English"),
            ("ru", LocalizedContentManager.LanguageCode.ru, "Русский"),
            ("zh", LocalizedContentManager.LanguageCode.zh, "中文"),
            ("de", LocalizedContentManager.LanguageCode.de, "Deutsch"),
            ("pt", LocalizedContentManager.LanguageCode.pt, "Português"),
            ("fr", LocalizedContentManager.LanguageCode.fr, "Français"),
            ("es", LocalizedContentManager.LanguageCode.es, "Español"),
            ("ja", LocalizedContentManager.LanguageCode.ja, "日本語"),
            ("ko", LocalizedContentManager.LanguageCode.ko, "한국어"),
            ("it", LocalizedContentManager.LanguageCode.it, "Italiano"),
            ("tr", LocalizedContentManager.LanguageCode.tr, "Türkçe"),
            ("hu", LocalizedContentManager.LanguageCode.hu, "Magyar"),
        };

        /// <summary>The language codes from <see cref="Languages"/>, so other config surfaces (e.g. the GMCM dropdown) offer exactly the same set instead of keeping their own list that can drift.</summary>
        public static string[] SupportedLanguageCodes => Languages.Select(entry => entry.Code).ToArray();

        /// <summary>Keys already bound to a vanilla action by default (see Options.setControlsToDefault): movement, tool use, menus, inventory slots, etc. We don't let the player rebind our hotkeys onto these, to avoid silently breaking basic gameplay controls.</summary>
        private static readonly HashSet<SButton> ReservedKeys = new()
        {
            SButton.W, SButton.A, SButton.S, SButton.D,
            SButton.Up, SButton.Down, SButton.Left, SButton.Right,
            SButton.X, SButton.V, SButton.C, SButton.E, SButton.Escape,
            SButton.LeftShift, SButton.T, SButton.OemQuestion,
            SButton.M, SButton.F, SButton.Y, SButton.Tab,
            SButton.D0, SButton.D1, SButton.D2, SButton.D3, SButton.D4,
            SButton.D5, SButton.D6, SButton.D7, SButton.D8, SButton.D9,
            SButton.OemMinus, SButton.OemPlus,
        };

        private const int MenuWidth = 820;
        private const int MenuHeight = 620;
        private const int Padding = 32;
        private const int TopPadding = 56;
        private const int BottomPadding = 32;
        private const int ScrollbarReservedWidth = 24;
        private const int ScrollSpeedPixels = 60;
        private const int LineGap = 4;
        private const int Columns = 3;
        private const int ButtonWidth = 220;
        private const int ButtonHeight = 44;
        private const int ButtonGapX = 12;
        private const int ButtonGapY = 10;
        private const int HotkeyButtonWidth = 170;

        private readonly ModConfig config;
        private readonly Action save;
        private readonly SpriteFont font;

        /// <summary>Resolves a font able to render a given language, so each language button can be labelled in its own script - the menu's own font only covers the current display language's script plus Latin, so e.g. an English font can't draw 中文 or 한국어.</summary>
        private readonly Func<LocalizedContentManager.LanguageCode?, SpriteFont> fontResolver;

        private readonly List<(string Code, Rectangle Bounds)> languageButtons = new();
        private Rectangle hotKeyChangeButton;
        private Rectangle replayLogHotKeyChangeButton;
        private Rectangle resetHotkeysButton;
        private int scrollPixels;

        private enum CaptureTarget { None, HotKey, ReplayLogHotKey }
        private CaptureTarget capturing = CaptureTarget.None;
        private string? statusMessage;

        /// <summary>Whether this menu is currently waiting for a key press to bind to a hotkey. ModEntry checks this to know whether to forward the next raw SButton press here via <see cref="HandleKeyPress"/>, instead of treating it as a normal hotkey trigger.</summary>
        public bool IsCapturingKey => this.capturing != CaptureTarget.None;

        /// <param name="font">Loaded for the game's current display language, so it covers both this menu's ASCII labels and the localized tip text (a language pack's font always includes Latin glyphs alongside its own script).</param>
        /// <param name="fontResolver">Used to label each language button in its own script - see <see cref="fontResolver"/>.</param>
        public LanguageConfigMenu(ModConfig config, Action save, SpriteFont font, Func<LocalizedContentManager.LanguageCode?, SpriteFont> fontResolver)
            : base(
                (Game1.uiViewport.Width - MenuWidth) / 2,
                Math.Max(16, (Game1.uiViewport.Height - Math.Min(MenuHeight, Game1.uiViewport.Height - 32)) / 2),
                MenuWidth,
                Math.Min(MenuHeight, Game1.uiViewport.Height - 32),
                showUpperRightCloseButton: true)
        {
            this.config = config;
            this.save = save;
            this.font = font;
            this.fontResolver = fontResolver;
        }

        /// <summary>Re-centre the menu when the window is resized (e.g. toggling fullscreen). The base implementation scales the old position proportionally, which doesn't preserve centring - it left the menu hanging off the corner of the screen after switching back from fullscreen.</summary>
        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            this.RecalculateBounds();
        }

        private void RecalculateBounds()
        {
            this.height = Math.Min(MenuHeight, Game1.uiViewport.Height - 32);
            this.width = MenuWidth;
            this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
            this.yPositionOnScreen = Math.Max(16, (Game1.uiViewport.Height - this.height) / 2);

            // The close button's position was baked in relative to the old bounds.
            this.initializeUpperRightCloseButton();
        }

        public override void receiveScrollWheelAction(int direction)
        {
            int viewHeight = this.height - TopPadding - BottomPadding;
            int contentHeight = this.LayoutAndDraw(null, int.MinValue, int.MaxValue);
            int maxScroll = Math.Max(0, contentHeight - viewHeight);
            this.scrollPixels = Math.Clamp(this.scrollPixels - Math.Sign(direction) * ScrollSpeedPixels, 0, maxScroll);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            foreach ((string code, Rectangle bounds) in this.languageButtons)
            {
                if (!bounds.Contains(x, y))
                    continue;

                this.config.PreferredLanguage = code;
                this.save();
                this.statusMessage = null;
                if (playSound)
                    Game1.playSound("smallSelect");
                return;
            }

            if (this.hotKeyChangeButton.Contains(x, y))
            {
                this.capturing = CaptureTarget.HotKey;
                this.statusMessage = "Press a key for the toggle-language hotkey (Esc to cancel)...";
                if (playSound)
                    Game1.playSound("smallSelect");
                return;
            }

            if (this.replayLogHotKeyChangeButton.Contains(x, y))
            {
                this.capturing = CaptureTarget.ReplayLogHotKey;
                this.statusMessage = "Press a key for the dialogue-log hotkey (Esc to cancel)...";
                if (playSound)
                    Game1.playSound("smallSelect");
                return;
            }

            if (this.resetHotkeysButton.Contains(x, y))
            {
                // Take the defaults from a fresh ModConfig rather than repeating literal key names
                // here, so this can't drift out of sync with the actual defaults.
                var defaults = new ModConfig();
                this.config.HotKey = defaults.HotKey;
                this.config.ReplayLogHotKey = defaults.ReplayLogHotKey;
                this.save();

                this.capturing = CaptureTarget.None;
                this.statusMessage = $"Hotkeys reset to defaults ({defaults.HotKey} / {defaults.ReplayLogHotKey}).";
                if (playSound)
                    Game1.playSound("drumkit6");
            }
        }

        /// <summary>Handle a captured key press while waiting for a new hotkey binding. Called by ModEntry from SMAPI's Input.ButtonPressed event, since that gives us the raw SButton directly - IClickableMenu's own receiveKeyPress only gets XNA's Keys, which would need converting back for gamepad/mouse-agnostic comparisons we don't need here anyway.</summary>
        public void HandleKeyPress(SButton button)
        {
            if (this.capturing == CaptureTarget.None)
                return;

            if (button == SButton.Escape)
            {
                this.capturing = CaptureTarget.None;
                this.statusMessage = null;
                return;
            }

            if (ReservedKeys.Contains(button))
            {
                this.statusMessage = $"'{button}' is already used by the game's default controls - pick a different key.";
                return;
            }

            string otherKey = this.capturing == CaptureTarget.HotKey
                ? this.config.ReplayLogHotKey.ToString()
                : this.config.HotKey.ToString();
            if (string.Equals(button.ToString(), otherKey, StringComparison.OrdinalIgnoreCase))
            {
                this.statusMessage = $"'{button}' is already used by the other hotkey below.";
                return;
            }

            KeybindList previous = this.capturing == CaptureTarget.HotKey
                ? this.config.HotKey
                : this.config.ReplayLogHotKey;
            bool replacedMultiBinding = IsBeyondSingleKey(previous);

            KeybindList newBinding = KeybindList.Parse(button.ToString());
            if (this.capturing == CaptureTarget.HotKey)
                this.config.HotKey = newBinding;
            else
                this.config.ReplayLogHotKey = newBinding;

            this.save();

            // 如果刚覆盖掉的是一个多键绑定，把原值写出来。这个菜单只能产生单键，所以覆盖是
            // 必然的；至少要让玩家看到丢掉了什么，以及去哪儿（GMCM / config.json）能改回来。
            this.statusMessage = replacedMultiBinding
                ? $"Hotkey set to '{button}', replacing '{previous}'. Use GMCM or config.json for multi-key bindings."
                : $"Hotkey set to '{button}'.";
            this.capturing = CaptureTarget.None;
            Game1.playSound("drumkit6");
        }

        public override void receiveKeyPress(Keys key)
        {
            // While waiting for a hotkey capture, swallow all keyboard input here instead of
            // letting the base menu handle it (e.g. Escape would otherwise close this whole menu
            // via Game1.options.menuButton, instead of just cancelling the capture like we want -
            // see HandleKeyPress, which handles the same key press through SMAPI's input event).
            if (this.capturing != CaptureTarget.None)
                return;

            base.receiveKeyPress(key);
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.6f);
            drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);

            int viewTop = this.yPositionOnScreen + TopPadding;
            int viewBottom = this.yPositionOnScreen + this.height - BottomPadding;

            int contentHeight = this.LayoutAndDraw(b, viewTop, viewBottom);
            this.DrawScrollbar(b, this.xPositionOnScreen + this.width - Padding + 4, viewTop, viewBottom, contentHeight);

            base.draw(b);
            this.drawMouse(b);
        }

        /// <summary>Lay out (and, if <paramref name="b"/> is not null, draw) all of this menu's content top to bottom, scrolled by <see cref="scrollPixels"/>. Also refreshes the stored clickable regions (language buttons, hotkey Change buttons) to their current on-screen position, since content scrolls - so their bounds have to be recomputed every frame, not just once at construction. Returns the total (unclamped) content height, used both to actually draw (b != null) and to measure the scroll range (b == null, called from receiveScrollWheelAction).</summary>
        private int LayoutAndDraw(SpriteBatch? b, int viewTop, int viewBottom)
        {
            this.languageButtons.Clear();

            int contentWidth = this.width - Padding * 2 - ScrollbarReservedWidth;
            int x = this.xPositionOnScreen + Padding;
            int y = this.yPositionOnScreen + TopPadding - this.scrollPixels;

            y = this.DrawLine(b, this.font, "Language Switcher Setup", x, y, Color.SaddleBrown, viewTop, viewBottom);
            y += 12;

            y = this.DrawWrapped(b, this.font, "Target language (toggles with whatever language you're currently in):", x, y, contentWidth, Game1.textColor, viewTop, viewBottom);
            y += 8;

            for (int i = 0; i < Languages.Length; i++)
            {
                int col = i % Columns;
                int row = i / Columns;
                var bounds = new Rectangle(x + col * (ButtonWidth + ButtonGapX), y + row * (ButtonHeight + ButtonGapY), ButtonWidth, ButtonHeight);
                this.languageButtons.Add((Languages[i].Code, bounds));

                // Only draw when the whole button fits in view, so a partially-scrolled row doesn't
                // leave a boxed button clipped across the menu border.
                if (b != null && bounds.Y >= viewTop && bounds.Bottom <= viewBottom)
                {
                    bool selected = string.Equals(Languages[i].Code, this.config.PreferredLanguage, StringComparison.OrdinalIgnoreCase);
                    drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height, selected ? Color.Wheat : Color.White);

                    // Label in the language's own script, which needs that language's own font -
                    // the menu font only covers the current display language plus Latin.
                    string label = $"{Languages[i].Code} - {Languages[i].Label}";
                    SpriteFont labelFont = this.fontResolver(Languages[i].Language);
                    this.DrawFitted(b, labelFont, label, bounds, selected ? Color.SaddleBrown : Color.Black);
                }
            }

            int rows = (int)Math.Ceiling(Languages.Length / (double)Columns);
            y += rows * ButtonHeight + (rows - 1) * ButtonGapY + 16;

            if (this.TryParseLanguageCode(this.config.PreferredLanguage, out LocalizedContentManager.LanguageCode preferred)
                && preferred == LocalizedContentManager.CurrentLanguageCode)
            {
                y = this.DrawWrapped(
                    b, this.font,
                    "Note: this matches the game's current language, so the hotkey won't visibly do anything until you pick a different target.",
                    x, y, contentWidth, Color.DarkRed, viewTop, viewBottom);
                y += 8;
            }

            y = this.DrawLine(b, this.font, "Hotkeys:", x, y, Game1.textColor, viewTop, viewBottom);
            y += 8;

            var defaults = new ModConfig();
            y = this.DrawHotkeyRow(b, "Toggle language", this.config.HotKey, defaults.HotKey.ToString(), x, y, contentWidth, ref this.hotKeyChangeButton, this.capturing == CaptureTarget.HotKey, viewTop, viewBottom);
            y = this.DrawHotkeyRow(b, "Dialogue replay log", this.config.ReplayLogHotKey, defaults.ReplayLogHotKey.ToString(), x, y, contentWidth, ref this.replayLogHotKeyChangeButton, this.capturing == CaptureTarget.ReplayLogHotKey, viewTop, viewBottom);

            this.resetHotkeysButton = new Rectangle(x + contentWidth - HotkeyButtonWidth, y - 4, HotkeyButtonWidth, this.font.LineSpacing + 8);
            if (b != null && this.resetHotkeysButton.Y >= viewTop && this.resetHotkeysButton.Bottom <= viewBottom)
            {
                drawTextureBox(b, this.resetHotkeysButton.X, this.resetHotkeysButton.Y, this.resetHotkeysButton.Width, this.resetHotkeysButton.Height, Color.White);
                this.DrawFitted(b, this.font, "Reset to default", this.resetHotkeysButton, Color.SaddleBrown);
            }
            y += this.font.LineSpacing + 16;
            y += 8;

            if (this.statusMessage != null)
            {
                y = this.DrawWrapped(b, this.font, this.statusMessage, x, y, contentWidth, Color.DarkSlateBlue, viewTop, viewBottom);
                y += 8;
            }

            // Shown in Chinese when that's the game's display language, English otherwise. Safe to
            // use this.font for both: it's loaded for the current display language, so it covers
            // Chinese exactly when we'd want to draw Chinese.
            bool chineseUi = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
            string help = chineseUi
                ? "提示：切换语言会在你之后触发的内容中生效。由于游戏在文字显示时就已确定语言，屏幕上已经显示出来的对话无法追溯翻译。想查看 NPC 对话的双语对照，可以使用对话回放日志（默认 L）。地图、电视等菜单在切换后重新打开即可显示新语言。"
                : "Tip: switching language takes effect for anything you interact with afterward. Text already drawn on screen keeps its original language, since the game resolves it at display time - to see NPC dialogue side by side, use the dialogue replay log (default L). Menus such as the map or TV will show the new language once reopened.";
            y = this.DrawWrapped(b, this.font, help, x, y, contentWidth, Color.Gray, viewTop, viewBottom);

            return y + this.scrollPixels - (this.yPositionOnScreen + TopPadding);
        }

        /// <summary>判断一个绑定是否超出本菜单能表达的范围——多组绑定（<c>K, LeftShift + K</c>）或组合键。</summary>
        /// <remarks>
        /// 配置字段是 <see cref="KeybindList"/>，本身支持多组绑定和组合键，GMCM 和直接编辑
        /// config.json 都能设。但本菜单捕获的是单个 <see cref="SButton"/>，写回去时只会产生一个
        /// 单键绑定——也就是说在这里点一次 Change，就会把原本的多重绑定悄悄压成单键。
        /// 检测到这种情况时在行尾标注出来，让覆盖是玩家知情的选择，而不是静默丢配置。
        /// </remarks>
        private static bool IsBeyondSingleKey(KeybindList binding)
        {
            return binding.Keybinds.Length > 1
                || binding.Keybinds.Any(keybind => keybind.Buttons.Length > 1);
        }

        private int DrawHotkeyRow(SpriteBatch? b, string label, KeybindList binding, string defaultValue, int x, int y, int contentWidth, ref Rectangle changeButton, bool isCapturing, int viewTop, int viewBottom)
        {
            string currentValue = binding.ToString();
            bool multiBinding = IsBeyondSingleKey(binding);
            int lineHeight = this.font.LineSpacing;
            changeButton = new Rectangle(x + contentWidth - HotkeyButtonWidth, y - 4, HotkeyButtonWidth, lineHeight + 8);

            // Only draw once the row fits *entirely* within the viewport. Unlike wrapped text -
            // where clipping an individual line mid-scroll looks fine - a hotkey row is a single
            // unit with a boxed button, and drawing it when only its top edge is in view left the
            // text and button visibly hanging past the menu's bottom border.
            if (b != null && changeButton.Bottom <= viewBottom && changeButton.Y >= viewTop)
            {
                string text = $"{label}: {currentValue} (default: {defaultValue})";
                if (multiBinding)
                    text += "  - multiple keys; Change replaces them with one";

                int maxTextWidth = contentWidth - HotkeyButtonWidth - 16;
                Vector2 textSize = this.font.MeasureString(text);
                float scale = Math.Min(1f, maxTextWidth / Math.Max(1f, textSize.X));
                b.DrawString(this.font, text, new Vector2(x, y), multiBinding ? Color.DarkRed : Color.Black, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

                drawTextureBox(b, changeButton.X, changeButton.Y, changeButton.Width, changeButton.Height, isCapturing ? Color.Wheat : Color.White);
                string buttonLabel = isCapturing ? "Press a key..." : "Change";
                this.DrawFitted(b, this.font, buttonLabel, changeButton, Color.SaddleBrown);
            }

            return y + lineHeight + 16;
        }

        /// <summary>Draw text centered within <paramref name="bounds"/>, scaled down (never up) so it always fits inside with a margin, regardless of the box's size or how long the text is - this is what was missing before that let button labels spill outside their own box.</summary>
        private void DrawFitted(SpriteBatch b, SpriteFont font, string text, Rectangle bounds, Color color)
        {
            Vector2 textSize = font.MeasureString(text);
            float scale = Math.Min(1f, (bounds.Width - 16) / Math.Max(1f, textSize.X)) * 0.92f;
            Vector2 pos = new(
                bounds.X + (bounds.Width - textSize.X * scale) / 2,
                bounds.Y + (bounds.Height - textSize.Y * scale) / 2);
            b.DrawString(font, text, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        private int DrawLine(SpriteBatch? b, SpriteFont font, string text, int x, int y, Color color, int viewTop, int viewBottom)
        {
            int lineHeight = font.LineSpacing;
            if (b != null && y + lineHeight >= viewTop && y <= viewBottom)
                b.DrawString(font, text, new Vector2(x, y), color);
            return y + lineHeight;
        }

        private int DrawWrapped(SpriteBatch? b, SpriteFont font, string text, int x, int y, int maxWidth, Color color, int viewTop, int viewBottom)
        {
            int lineHeight = font.LineSpacing + LineGap;
            foreach (string line in WrapText(font, text, maxWidth))
            {
                if (b != null && y + lineHeight >= viewTop && y <= viewBottom)
                    b.DrawString(font, line, new Vector2(x, y), color);
                y += lineHeight;
            }
            return y;
        }

        /// <summary>Draw a vertical scrollbar track + thumb, same pattern as DialogueLogMenu, so it's clear how far up/down the menu the player currently is.</summary>
        private void DrawScrollbar(SpriteBatch b, int x, int viewTop, int viewBottom, int contentHeight)
        {
            int viewHeight = viewBottom - viewTop;
            if (contentHeight <= viewHeight)
                return;

            const int trackWidth = 6;
            b.Draw(Game1.staminaRect, new Rectangle(x, viewTop, trackWidth, viewHeight), Color.SaddleBrown * 0.25f);

            int maxScroll = contentHeight - viewHeight;
            int thumbHeight = Math.Max(24, viewHeight * viewHeight / contentHeight);
            int thumbY = viewTop + (viewHeight - thumbHeight) * this.scrollPixels / Math.Max(1, maxScroll);
            b.Draw(Game1.staminaRect, new Rectangle(x, thumbY, trackWidth, thumbHeight), Color.SaddleBrown);
        }

        private bool TryParseLanguageCode(string value, out LocalizedContentManager.LanguageCode code)
        {
            return Enum.TryParse(value, ignoreCase: true, out code) && code != LocalizedContentManager.LanguageCode.mod;
        }

        /// <summary>Break text into lines that fit within <paramref name="maxWidth"/>. Wraps at spaces for Latin text and between individual characters for CJK/Hangul/fullwidth text, which don't use spaces between words. Duplicated from DialogueLogMenu rather than shared, since both menus are small and self-contained.</summary>
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

        private static bool IsWideCharacter(char c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF)
                || (c >= 0x3040 && c <= 0x30FF)
                || (c >= 0xAC00 && c <= 0xD7A3)
                || (c >= 0xFF00 && c <= 0xFFEF);
        }
    }
}
