using StardewModdingAPI.Utilities;

namespace LanguageSwitcher
{
    public class ModConfig
    {
        /// <summary>The key or key combination that toggles the game's language.</summary>
        /// <remarks>Defaults to K instead of the more obvious F12, since F12 is Steam's default screenshot hotkey.</remarks>
        public KeybindList HotKey { get; set; } = KeybindList.Parse("K");

        /// <summary>The non-English language to toggle to (a <see cref="StardewValley.LocalizedContentManager.LanguageCode"/> name, e.g. "zh"). The hotkey toggles between this and English.</summary>
        public string PreferredLanguage { get; set; } = "zh";

        /// <summary>Whether to show an on-screen message after switching languages.</summary>
        public bool ShowNotifications { get; set; } = true;

        /// <summary>How many seconds the switch notification stays on screen.</summary>
        public int NotificationDuration { get; set; } = 3;

        /// <summary>The key or key combination that opens the bilingual dialogue replay log.</summary>
        public KeybindList ReplayLogHotKey { get; set; } = KeybindList.Parse("L");
    }
}
