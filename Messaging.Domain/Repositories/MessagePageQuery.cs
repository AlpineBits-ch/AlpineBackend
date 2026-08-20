namespace Messaging.Domain.Repositories;

/// <summary>Which direction a cursor page runs relative to its anchor message.</summary>
public enum MessageCursorDirection
{
    /// <summary>Messages older than the anchor - the "scroll up through history" case.</summary>
    Before,

    /// <summary>Messages newer than the anchor - catching a client up after a gap.</summary>
    After,

    /// <summary>Half a page either side of the anchor, plus the anchor itself.</summary>
    Around,
}

/// <summary>A cursor-anchored page request.</summary>
public record MessagePageQuery
{
    public required string ContextId { get; init; }

    /// <summary>
    /// Anchor message id, or null with <see cref="MessageCursorDirection.After"/> to mean the
    /// beginning of the context. There is no message to anchor on before the first one, and a
    /// scene channel is created without a starter message.
    /// </summary>
    public required string? AnchorMessageId { get; init; }

    public required MessageCursorDirection Direction { get; init; }

    public int Limit { get; init; } = 50;
}
