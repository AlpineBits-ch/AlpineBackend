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

    /// <summary>The message's stored CreatedAt, carried across from Messaging. Guild writes it
    /// straight onto Channel.LastActivityAt, so it has to be the stored value rather than a
    /// downstream UtcNow.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public MessageEncryptionState EncryptionState { get; set; }

    /// <summary>Which MLS group generation the ciphertext was sealed under; null on plaintext.
    /// Travels with the message so a client decrypting from a push notification can pick the right
    /// group - encryption can be toggled off and on, and each stretch is a distinct group whose
    /// epochs restart at zero, so the epoch alone is ambiguous.</summary>
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
/// Published by Messaging.Application when a channel (non-conversation) message is edited -
/// mirrors MessageCreatedForChannel. Guild.Application resolves GuildId from this and republishes
/// <see cref="MessageUpdatedForBots"/> for Bots.Application, plus broadcasts guild.MessageUpdated
/// to guild members over the hub.
/// </summary>
public class MessageUpdatedForChannel
{
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public byte[] Content { get; set; }
    public string AuthorId { get; set; }
    public string? EmbedsJson { get; set; }

    /// <summary>Interactive components. Carried alongside EmbedsJson, which it was missing while
    /// only embeds travelled - so a component change reached the database but never a client.</summary>
    public string? ComponentsJson { get; set; }

    /// <summary>Discord-compatible message flags. Clients and bots need SUPPRESS_EMBEDS to tell
    /// "the author dismissed the preview" apart from "the preview failed to generate" - both
    /// otherwise arrive as an empty embeds array.</summary>
    public int Flags { get; set; }

    /// <summary>When the author last edited the text, or null if never. Distinct from the row's
    /// UpdatedAt: attaching a link preview rewrites the row without anybody having edited it, so
    /// clients must render "(edited)" from this and not from the update itself arriving.</summary>
    public DateTimeOffset? EditedAt { get; set; }

    /// <summary>Whether the author caused this update. False for a server-attached link preview or
    /// a moderator suppression - which is what tells the channel broadcast to include the author
    /// rather than skip them as it does for their own edits.</summary>
    public bool IsAuthorEdit { get; set; } = true;
}


/// <summary>
/// Published by Messaging.Application when a channel (non-conversation) message is pinned/unpinned -
/// mirrors MessageUpdatedForChannel. Guild.Application resolves GuildId from this, broadcasts
/// guild.MessagePinned/guild.MessageUnpinned to guild members over the hub, and writes an audit
/// log entry (pinning is moderation-adjacent, gated by Permissions.PinMessages).
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