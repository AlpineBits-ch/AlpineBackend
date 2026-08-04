using Messaging.Domain.Enums;

namespace Messaging.Domain.Previews;

/// <summary>
/// Whether a message should have its links resolved into previews.
///
/// <para>A pure predicate rather than a handler, and that is the whole point. The obvious design
/// was a second handler class subscribing to <c>MessageCreated</c> alongside
/// <c>MessageCreatedHandler</c> - and it silently never ran. No message type in this codebase had
/// ever been handled by two handler classes before (Guild, Social and Identity have zero such
/// cases between them), and the shared Wolverine setup pairs
/// <c>MultipleHandlerBehavior.Separated</c> with <c>NamingSource.FromHandlerType</c>, so the second
/// handler for an already-handled type never got a listener of its own. Nothing failed; the feature
/// simply did nothing.</para>
///
/// <para>So the decision lives here, the two handlers that <i>demonstrably do</i> run call it, and
/// every message type keeps exactly one handler class - the arrangement the rest of the service
/// already relies on.</para>
/// </summary>
public static class UnfurlDecision
{
    /// <summary>
    /// Whether a newly-created message is a candidate.
    ///
    /// <para>Kept cheap: this runs on every message posted on the instance, and the overwhelming
    /// majority contain no link at all.</para>
    /// </summary>
    public static bool ShouldUnfurlNew(
        MessageEncryptionState encryptionState,
        MessageType type,
        string? embedsJson,
        byte[]? content)
    {
        // Encrypted contexts: Content is MLS ciphertext, so there is no URL for the server to see.
        // The same wall that stops search indexing - a property of E2EE, not a gap to work around.
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
    ///
    /// <para><b><paramref name="isAuthorEdit"/> is what stops this looping.</b> Attaching a preview
    /// is itself a write, and it publishes <c>MessageUpdated</c> like any other - so reacting to
    /// every update would unfurl, publish, unfurl, publish, forever. Only a human (or a federated
    /// remote author) changing the text sets it. Suppression also arrives with it false, which is
    /// the second thing this buys: dismissing a preview must not immediately rebuild it.</para>
    /// </summary>
    public static bool ShouldUnfurlEdit(bool isAuthorEdit, int flags, byte[]? content)
    {
        if (!isAuthorEdit) return false;
        if (MessageFlags.Has(flags, MessageFlags.SuppressEmbeds)) return false;

        return LinkExtractor.Extract(content).Count > 0;
    }
}
