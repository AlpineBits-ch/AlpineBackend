namespace Guild.Contracts.Bus.Events;

/// <summary>
/// Republished by Guild.Application's MessageCreatedForChannel handler once it has resolved
/// ChannelId -> GuildId (the raw MessageCreatedForChannel event carries no GuildId - Messaging
/// doesn't own that mapping).
/// </summary>
public class MessageCreatedForBots
{
    public string GuildId { get; set; }
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public byte[] Content { get; set; }
    public string AuthorId { get; set; }
    public MessageEncryptionState EncryptionState { get; set; }

    /// <summary>Raw JSON array of Discord-shaped embeds - see Messaging.Domain.Entities.Message.EmbedsJson.</summary>
    public string? EmbedsJson { get; set; }

    public MessageType Type { get; set; } = MessageType.Message;
    public int? SystemMessageVariant { get; set; }

    // The author identity the bot gateway renders. Carried separately from MessageCreatedForChannel
    // because Bots.Application consumes this event and not that one, and without these four a
    // webhook or a character reaches bots looking like an ordinary account post.
    public AuthorIdType AuthorIdType { get; set; } = AuthorIdType.User;
    public string? AuthorDisplayName { get; set; }
    public string? AuthorAvatarUrl { get; set; }
    public string? PersonaId { get; set; }
}

/// <summary>
/// Republished by Guild.Application once it has resolved a deleted message's ChannelId ->
/// GuildId (mirrors <see cref="MessageCreatedForBots"/>).
/// </summary>
public class MessageDeletedForBots
{
    public string GuildId { get; set; }
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
}

/// <summary>
/// Republished by Guild.Application once it has resolved an edited message's ChannelId ->
/// GuildId (mirrors <see cref="MessageCreatedForBots"/>).
/// </summary>
public class MessageUpdatedForBots
{
    public string GuildId { get; set; }
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public byte[] Content { get; set; }
    public string AuthorId { get; set; }
    public string? EmbedsJson { get; set; }

    /// <summary>Interactive components - see <see cref="MessageUpdatedForChannel.ComponentsJson"/>.</summary>
    public string? ComponentsJson { get; set; }

    /// <summary>
    /// Discord-compatible message flags, surfaced on the bot gateway's MESSAGE_UPDATE.
    /// </summary>
    public int Flags { get; set; }

    /// <summary>Discord's <c>edited_timestamp</c>.</summary>
    public DateTimeOffset? EditedAt { get; set; }

    /// <inheritdoc cref="MessageCreatedForBots.AuthorIdType"/>
    public AuthorIdType AuthorIdType { get; set; } = AuthorIdType.User;
    public string? AuthorDisplayName { get; set; }
    public string? AuthorAvatarUrl { get; set; }
    public string? PersonaId { get; set; }
}

/// <summary>
/// Published by Messaging.Application when a channel (non-conversation) message is deleted -
/// mirrors the existing MessageCreatedForChannel.
/// </summary>
public class MessageDeletedForChannel
{
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public string AuthorId { get; set; }
}

/// <summary>
/// Published once per bulk-delete call, in addition to the individual MessageDeleted events the
/// same call fans out.
/// </summary>
public class MessagesBulkDeletedForChannel
{
    public string ChannelId { get; set; }
    public List<string> MessageIds { get; set; } = [];
    public string ActorUserId { get; set; }
}

/// <summary>Guild-resolved form of <see cref="MessagesBulkDeletedForChannel"/>, consumed by
/// Bots.Application to dispatch Discord's MESSAGE_DELETE_BULK.</summary>
public class MessagesBulkDeletedForBots
{
    public string GuildId { get; set; }
    public string ChannelId { get; set; }
    public List<string> MessageIds { get; set; } = [];
}
