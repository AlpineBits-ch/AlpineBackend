namespace Guild.Contracts.Bus.Events;

/// <summary>
/// Republished by Guild.Application's MessageCreatedForChannel handler once it has resolved
/// ChannelId -> GuildId (the raw MessageCreatedForChannel event carries no GuildId - Messaging
/// doesn't own that mapping). Bots.Application subscribes to this, not the raw event, so it
/// gets the guild id for free instead of doing its own channel->guild lookup.
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
}

/// <summary>
/// Published by Messaging.Application when a channel (non-conversation) message is deleted -
/// mirrors the existing MessageCreatedForChannel. Guild.Application resolves GuildId from this
/// and republishes <see cref="MessageDeletedForBots"/> for Bots.Application to consume.
/// </summary>
public class MessageDeletedForChannel
{
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public string AuthorId { get; set; }
}

/// <summary>
/// Published once per bulk-delete call, *in addition to* the individual MessageDeleted events the
/// same call fans out. The per-message events carry every side effect (search index, bot
/// MESSAGE_DELETE, forum reply counts); this one exists purely so connected clients can drop a
/// whole range in a single UI update instead of processing N removals, and so bots receive
/// Discord's MESSAGE_DELETE_BULK shape. Guild.Application resolves GuildId and republishes.
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
