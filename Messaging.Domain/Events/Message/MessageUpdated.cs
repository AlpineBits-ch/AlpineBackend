using Domain;
using Messaging.Domain.Enums;

namespace Messaging.Domain.Events.Message;

public class MessageUpdated : DomainEvent
{
    public string MessageId { get; set; }
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public byte[] Content { get; set; }
    public string AuthorId { get; set; }
    public string? EmbedsJson { get; set; }

    /// <summary>Interactive components.</summary>
    public string? ComponentsJson { get; set; }

    /// <summary>Message flag bitfield (<c>Messaging.Domain.Enums.MessageFlags</c>).</summary>
    public int Flags { get; set; }

    /// <summary>Row-touch time. Distinct from <see cref="EditedAt"/> - see Message.EditedAt.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the author last changed the text, or null if never.</summary>
    public DateTimeOffset? EditedAt { get; set; }

    /// <summary>Whether the author caused this update.</summary>
    public bool IsAuthorEdit { get; set; } = true;

    public AuthorIdType AuthorIdType { get; set; } = AuthorIdType.User;

    /// <summary>Per-message author name override - see Entities.Message.AuthorDisplayName.</summary>
    public string? AuthorDisplayName { get; set; }

    /// <summary>Per-message author avatar override - see Entities.Message.AuthorAvatarUrl.</summary>
    public string? AuthorAvatarUrl { get; set; }

    /// <summary>Which character the message was spoken as - see Entities.Message.PersonaId. An edit
    /// never re-resolves it, so this is whatever the message was sent under.</summary>
    public string? PersonaId { get; set; }
}