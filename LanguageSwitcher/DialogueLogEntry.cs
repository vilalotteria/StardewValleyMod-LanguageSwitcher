using StardewValley;

namespace LanguageSwitcher
{
    /// <summary>A single captured line of NPC dialogue, with a best-effort translation looked up at capture time, for the bilingual replay log.</summary>
    internal sealed record DialogueLogEntry(
        string Speaker,
        string? TranslationKey,
        string Text,
        LocalizedContentManager.LanguageCode Language,
        string? TranslatedText,
        LocalizedContentManager.LanguageCode? TranslatedLanguage,
        bool IsPlayerOption = false
    );
}
