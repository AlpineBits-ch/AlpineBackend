namespace Messaging.Application.Dtos.Request;

/// <summary>Body for PATCH /api/v1/messaging/{messageId}/embeds.</summary>
public class SuppressEmbedsDto
{
    /// <summary>True hides this message's previews for everyone; false restores them and re-queues
    /// an unfurl. A body is required rather than making this two verbs, so the client can toggle
    /// idempotently without tracking which state it thinks the message is in.</summary>
    public bool Suppress { get; set; } = true;
}
