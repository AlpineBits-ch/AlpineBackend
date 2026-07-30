namespace Messaging.Domain.Repositories;

/// <summary>Which direction a cursor page runs relative to its anchor message.</summary>
public enum MessageCursorDirection
{
    /// <summary>Messages older than the anchor - the "scroll up through history" case.</summary>
    Before,

    /// <summary>Messages newer than the anchor - catching a client up after a gap.</summary>
    After,

    /// <summary>Half a page either side of the anchor, plus the anchor itself. What
    /// jump-to-message and permalinks need, and the one shape offset paging cannot express.</summary>
    Around,
}

/// <summary>
/// A cursor-anchored page request. The anchor is a message *id*, not a timestamp: ids are what
/// clients already hold (from a permalink, a reply reference, or the last message they rendered),
/// and the repository resolves the id to its sort position itself.
///
/// This exists alongside - not instead of - the older (take, skip) overloads. Offset paging over a
/// live channel silently drifts as messages arrive underneath the reader, so it is deprecated for
/// new callers, but removing it would break every shipped client at once.
/// </summary>
public record MessagePageQuery
{
    public required string ContextId { get; init; }

    /// <summary>Anchor message id. Its own position is resolved by the repository; for
    /// <see cref="MessageCursorDirection.Around"/> the anchor is included in the result.</summary>
    public required string AnchorMessageId { get; init; }

    public required MessageCursorDirection Direction { get; init; }

    public int Limit { get; init; } = 50;
}
