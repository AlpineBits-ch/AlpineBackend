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

    /// <summary>Interactive components. Was missing here while <c>EmbedsJson</c> was carried, so a
    /// component-only change reached the database but never the clients.</summary>
    public string? ComponentsJson { get; set; }

    /// <summary>Message flag bitfield (<c>Messaging.Domain.Enums.MessageFlags</c>). Clients need
    /// this to tell "the author suppressed the preview" apart from "the preview failed to
    /// generate" - both otherwise arrive as an empty embeds array.</summary>
    public int Flags { get; set; }

    /// <summary>Row-touch time. Distinct from <see cref="EditedAt"/> - see Message.EditedAt.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the author last changed the text, or null if never. Clients render the
    /// "(edited)" marker from this, <b>not</b> from <see cref="UpdatedAt"/>, so that attaching a
    /// link preview does not label the message as edited.</summary>
    public DateTimeOffset? EditedAt { get; set; }

    /// <summary>
    /// Whether the author caused this update.
    ///
    /// <para>Drives one thing: whether the author is included in the realtime broadcast. They are
    /// normally excluded, on the assumption that their own client already rendered the edit it just
    /// made. That assumption breaks completely for a server-generated link preview - the author
    /// did not make that change and their client knows nothing about it, so excluding them means
    /// the person who posted the link is the one person who never sees its preview.</para>
    /// </summary>
    public bool IsAuthorEdit { get; set; } = true;
}