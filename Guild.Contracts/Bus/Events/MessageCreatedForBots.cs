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
/// Published by Messaging.Application when a channel (non-conversation) message is deleted -
/// mirrors the existing MessageCreatedForChannel.
/// </summary>
public class MessageDeletedForChannel
{
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public string AuthorId { get; set; }
}
