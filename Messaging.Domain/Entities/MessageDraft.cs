using System.ComponentModel.DataAnnotations.Schema;
using Persistence;

namespace Messaging.Domain.Entities;

/// <summary>What one person has typed into one channel or conversation and not yet sent.</summary>
public class MessageDraft : BaseEntity<MessageDraft>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "drft";

    /// <summary>The author, and the only account that may ever read this row.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The channel or conversation this draft belongs to, mirroring Message.ContextId.</summary>
    public string ContextId { get; set; } = string.Empty;

    /// <summary>Set for a guild channel draft, null for a conversation one.</summary>
    public string? ChannelId { get; set; }

    /// <summary>Set for a DM or group draft, null for a channel one.</summary>
    public string? ConversationId { get; set; }

    /// <summary>The body as typed. Plaintext, and deliberately so: a draft is not a message and has
    /// no MLS generation to seal it under, so it is stored under the same protection the rest of
    /// this database gets rather than pretending to be end-to-end encrypted.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>The message this draft is a reply to, so the reply survives a refresh along with
    /// the text - losing it is what forces somebody to go and find the post again.</summary>
    public string? InReplyTo { get; set; }

    /// <summary>Replaces the stored body wholesale.</summary>
    /// <param name="content">The new body.</param>
    /// <param name="inReplyTo">The new reply target, or null to clear it.</param>
    public void Replace(string content, string? inReplyTo)
    {
        Content = content;
        InReplyTo = inReplyTo;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>A draft for a context the author has not written in before.</summary>
    /// <param name="userId">The author.</param>
    /// <param name="channelId">The channel, or null for a conversation draft.</param>
    /// <param name="conversationId">The conversation, or null for a channel draft.</param>
    /// <param name="content">The body as typed.</param>
    /// <param name="inReplyTo">The message being replied to, or null.</param>
    /// <returns>The new draft.</returns>
    /// <exception cref="ArgumentException">Neither or both of the two context ids were supplied.</exception>
    public static MessageDraft Create(
        string userId, string? channelId, string? conversationId, string content, string? inReplyTo)
    {
        var contextId = (channelId, conversationId) switch
        {
            ({ Length: > 0 }, null or "") => channelId!,
            (null or "", { Length: > 0 }) => conversationId!,
            _ => throw new ArgumentException("A draft belongs to exactly one channel or conversation."),
        };

        var now = DateTimeOffset.UtcNow;

        return new MessageDraft
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            UserId = userId,
            ContextId = contextId,
            ChannelId = string.IsNullOrEmpty(channelId) ? null : channelId,
            ConversationId = string.IsNullOrEmpty(conversationId) ? null : conversationId,
            Content = content,
            InReplyTo = inReplyTo,
        };
    }
}
