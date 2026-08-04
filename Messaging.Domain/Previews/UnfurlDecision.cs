using Messaging.Domain.Enums;

namespace Messaging.Domain.Previews;

/// <summary>Whether a message should have its links resolved into previews.</summary>
public static class UnfurlDecision
{
    /// <summary>Whether a newly-created message is a candidate.</summary>
    public static bool ShouldUnfurlNew(
        MessageEncryptionState encryptionState,
        MessageType type,
        string? embedsJson,
        byte[]? content)
    {
        // Encrypted contexts: Content is MLS ciphertext, so there is no URL for the server to see.
        if (encryptionState != MessageEncryptionState.Plain) return false;

        // Join/leave/invite system messages have no user-authored body to unfurl.
        if (type != MessageType.Message) return false;

        // A bot or webhook that posted its own cards is left alone: Discord does not add link
        // previews on top of author-supplied embeds, and there is no sensible way to order the two.
        if (GeneratedEmbeds.HasAuthorEmbeds(embedsJson)) return false;

        return LinkExtractor.Extract(content).Count > 0;
    }

    /// <summary>
    /// Whether an edited message should be re-unfurled, so changing a link changes the card.
    /// </summary>
    public static bool ShouldUnfurlEdit(bool isAuthorEdit, int flags, byte[]? content)
    {
        if (!isAuthorEdit) return false;
        if (MessageFlags.Has(flags, MessageFlags.SuppressEmbeds)) return false;

        return LinkExtractor.Extract(content).Count > 0;
    }
}
