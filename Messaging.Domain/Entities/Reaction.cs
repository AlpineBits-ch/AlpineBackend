using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Messaging.Domain.Entities;

public class CreateReactionParams
{
    public string MessageId { get; set; }
    public string Emoji { get; set; }
    public string UserId { get; set; }
    public string? ConversationId { get; set; }
    public string? ChannelId { get; set; }
}
public class Reaction
{
    public string ContextId { get; set; }
    public string MessageId { get; set; }
    public string Emoji { get; set; }
    public string UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ConversationId { get; set; }
    public string? ChannelId { get; set; }

    public static Reaction Create(CreateReactionParams createReactionParams)
    {
        if(!IsSingleEmoji(createReactionParams.Emoji)) throw new ArgumentException("Reaction must be a single emoji", nameof(createReactionParams.Emoji));

        string contextId = string.Empty;
        if(!string.IsNullOrEmpty(createReactionParams.ConversationId)) contextId = createReactionParams.ConversationId;
        else if(!string.IsNullOrEmpty(createReactionParams.ChannelId)) contextId = createReactionParams.ChannelId;
        else throw new ArgumentNullException(nameof(createReactionParams.ConversationId), "Reaction must have a conversation or channel id");
        
        return new Reaction()
        {
            ContextId = contextId,
            MessageId = createReactionParams.MessageId,
            Emoji = createReactionParams.Emoji,
            UserId = createReactionParams.UserId,
            CreatedAt = DateTime.UtcNow,
            ConversationId = createReactionParams.ConversationId,
            ChannelId = createReactionParams.ChannelId
        };
    }
    public static bool IsSingleEmoji(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var enumerator = StringInfo.GetTextElementEnumerator(value);
        if (!enumerator.MoveNext()) return false;

        var element = enumerator.GetTextElement();
        if (enumerator.MoveNext()) return false; // more than one grapheme cluster

        // Check if the grapheme cluster contains at least one emoji codepoint
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