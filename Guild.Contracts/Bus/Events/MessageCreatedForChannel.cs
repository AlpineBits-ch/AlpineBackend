namespace Guild.Contracts.Bus.Events;

public enum MessageEncryptionState
{
    Plain,
    Encrypted
}

/// <summary>Mirrors Messaging.Domain.Enums.MessageType - GuildMemberJoin/GuildMemberLeave carry no
/// real Content, clients render one of a fixed set of localized copy variants using
/// SystemMessageVariant instead (same convention as Discord's system messages).</summary>
public enum MessageType
{
    Message,
    Invite,
    GuildMemberJoin,
    GuildMemberLeave,
}

public class MessageCreatedForChannel
{
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public byte[] Content { get; set; }
    public string AuthorId { get; set; }
    public MessageEncryptionState EncryptionState { get; set; }

    /// <summary>
    /// Which MLS group generation the ciphertext was sealed under; null on plaintext.
    /// </summary>
    public int? MlsGeneration { get; set; }

    public ICollection<string> Mentions { get; set; } = new List<string>();
    public ICollection<string> RoleMentions { get; set; } = new List<string>();
    public bool MentionsEveryone { get; set; }
    public bool MentionsHere { get; set; }

    /// <summary>Raw JSON array of Discord-shaped embeds - see Messaging.Domain.Entities.Message.EmbedsJson.</summary>
    public string? EmbedsJson { get; set; }

    /// <summary>Raw JSON array of interactive components - see Message.ComponentsJson.</summary>
    public string? ComponentsJson { get; set; }

    public MessageType Type { get; set; } = MessageType.Message;
    public int? SystemMessageVariant { get; set; }

    public ICollection<MinimalAttachmentForChannel> Attachments { get; set; } = new List<MinimalAttachmentForChannel>();
}

/// <summary>
/// Published by Messaging.Application when a channel (non-conversation) message is edited - mirrors
/// MessageCreatedForChannel.
/// </summary>
public class MessageUpdatedForChannel
{
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public byte[] Content { get; set; }
    public string AuthorId { get; set; }
    public string? EmbedsJson { get; set; }
}


/// <summary>
/// Published by Messaging.Application when a channel (non-conversation) message is pinned/unpinned
/// - mirrors MessageUpdatedForChannel.
/// </summary>
public class MessagePinnedForChannel
{
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public string AuthorId { get; set; }
    public string PinnedById { get; set; }
    public DateTime PinnedAt { get; set; }
}

public class MessageUnpinnedForChannel
{
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public string AuthorId { get; set; }
    public string UnpinnedById { get; set; }
}

public class MinimalAttachmentForChannel
{
    public string Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string FileName { get; init; }
    public string ContentType { get; init; }
    public string? ThumbnailUrl { get; init; } = "";
    public string? ThumbnailId { get; init; }
    
    public static string GetCacheId(string id)
    {
        return $"attachment:{id}:thumb";
    }

}