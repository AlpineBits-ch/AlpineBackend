using System.Globalization;
using System.Text;

namespace Guild.Domain;

/// <summary>"Is this string exactly one emoji?" for the guild domain.</summary>
public static class EmojiText
{
    public static bool IsSingleEmoji(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var enumerator = StringInfo.GetTextElementEnumerator(value);
        if (!enumerator.MoveNext()) return false;

        var element = enumerator.GetTextElement();
        if (enumerator.MoveNext()) return false; // more than one grapheme cluster

        return element.EnumerateRunes().Any(IsEmojiRune);
    }

    private static bool IsEmojiRune(Rune rune)
    {
        int v = rune.Value;

        return v == 0x00A9 || v == 0x00AE                        // © ®
                           || (v >= 0x203C && v <= 0x3299)                      // misc symbols & dingbats
                           || (v >= 0x1F000 && v <= 0x1F9FF)                    // emoticons, misc symbols, transport, etc.
                           || (v >= 0x1FA00 && v <= 0x1FA9F)                    // chess, etc.
                           || (v >= 0x1FAA0 && v <= 0x1FAFF)                    // symbols & pictographs extended-A
                           || (v >= 0x2600  && v <= 0x27BF)                     // misc symbols, dingbats
                           || (v >= 0x2300  && v <= 0x23FF)                     // misc technical
                           || (v >= 0xFE00  && v <= 0xFE0F)                     // variation selectors
                           || (v >= 0x1F1E0 && v <= 0x1F1FF);                   // regional indicator symbols (flags)
    }
}
