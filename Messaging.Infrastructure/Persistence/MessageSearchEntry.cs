using System.ComponentModel.DataAnnotations;
using NpgsqlTypes;

namespace Messaging.Infrastructure.Persistence;

/// <summary>Postgres-only full-text search index, populated alongside message creation regardless
/// of whether the message content itself lives in Scylla or Postgres (see IMessageRepository) -
/// search needs Postgres's tsvector support either way, so this lives in Infrastructure rather
/// than Domain even though Message/Reaction do not. Only Plain-encryption messages get indexed;
/// there is nothing to search in an MLS-encrypted ciphertext blob server-side.</summary>
public class MessageSearchEntry
{
    [Key]
    public string MessageId { get; set; } = null!;
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public string AuthorId { get; set; } = null!;
    public string Content { get; set; } = null!;
    public DateTime CreatedAt { get; set; }

    public NpgsqlTsVector SearchVector { get; set; } = null!;
}
