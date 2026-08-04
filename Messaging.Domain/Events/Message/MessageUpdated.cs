using Domain;

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
}