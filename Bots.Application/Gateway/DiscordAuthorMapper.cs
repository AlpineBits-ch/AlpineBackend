using Bots.Contracts.Gateway.Payloads;
using Guild.Contracts.Bus.Events;

namespace Bots.Application.Gateway;

/// <summary>Everything a message says about who spoke, whichever bus event delivered it.</summary>
/// <param name="AuthorIdType">Which kind of author Messaging recorded.</param>
/// <param name="AuthorId">The account, webhook or bot id the message is stored under.</param>
/// <param name="AuthorDisplayName">Per-message name override, set for webhook executions and persona posts.</param>
/// <param name="AuthorAvatarUrl">Per-message avatar override, paired with the name.</param>
/// <param name="PersonaId">The persona that spoke, null for every other author and for federated characters.</param>
/// <param name="ResolvedUserName">The account's own username, looked up from Identity.</param>
/// <param name="ResolvedIsBot">Whether that account is a bot account.</param>
public record DiscordAuthorSource(
    AuthorIdType AuthorIdType,
    string AuthorId,
    string? AuthorDisplayName,
    string? AuthorAvatarUrl,
    string? PersonaId,
    string? ResolvedUserName,
    bool ResolvedIsBot);

/// <summary>Maps Echo's author type and per-message display overrides onto the Discord-compatible
/// author fields of a message payload.</summary>
public static class DiscordAuthorMapper
{
    // The persona mapping is decided here and nowhere else. A persona borrows Discord's
    // webhook-message shape - webhook_id set, the character in author.username and author.avatar -
    // because that is the only shape discord.js already renders as a name that is not the account's,
    // and because a present webhook_id is what stops it caching the costume under the real account
    // (Message's constructor passes !data.webhook_id as the cache flag). What it does not borrow is
    // author.id: Discord puts the webhook id there and the human is then unrecoverable, which is the
    // one property personas exist to keep, so author.id stays the real account. That leaves a
    // standard-fields tell, since author.id equals webhook_id for a real webhook and never for a
    // persona, but author_type is the authoritative answer and is always sent - the inequality is an
    // inference, and a federated character has no persona id to put in webhook_id at all.

    /// <summary>Fills in the author object, webhook_id, author_type and the persona block.</summary>
    /// <param name="payload">The message payload being dispatched.</param>
    /// <param name="source">What the message records about its author.</param>
    public static void Apply(MessageCreatePayload payload, DiscordAuthorSource source)
    {
        payload.Author = new DiscordUserPayload
        {
            Id = source.AuthorId,
            Username = source.AuthorDisplayName ?? source.ResolvedUserName ?? source.AuthorId,
            Avatar = source.AuthorAvatarUrl,
            // False for a persona: the near-universal `if (author.bot) return` guard is there to
            // stop bot loops, and a human typing in character cannot cause one.
            Bot = source.AuthorIdType is AuthorIdType.Bot or AuthorIdType.Webhook || source.ResolvedIsBot,
        };

        payload.AuthorType = source.AuthorIdType switch
        {
            AuthorIdType.Bot => DiscordAuthorType.Bot,
            AuthorIdType.Webhook => DiscordAuthorType.Webhook,
            AuthorIdType.Persona => DiscordAuthorType.Persona,
            _ => DiscordAuthorType.User,
        };

        payload.WebhookId = source.AuthorIdType switch
        {
            AuthorIdType.Webhook => source.AuthorId,
            AuthorIdType.Persona => source.PersonaId ?? source.AuthorId,
            _ => null,
        };

        if (source.AuthorIdType != AuthorIdType.Persona) return;

        payload.Persona = new MessagePersonaPayload
        {
            Id = source.PersonaId,
            Name = payload.Author.Username,
            AvatarUrl = source.AuthorAvatarUrl,
        };
    }
}
