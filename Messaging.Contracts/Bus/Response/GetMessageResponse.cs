namespace Messaging.Contracts.Bus.Response;

/// <summary>
/// Deliberately a narrow projection rather than the whole Message entity: the only consumer needs
/// the author, the channel and the components, and widening a cross-service contract to "the
/// entity" is how a storage-layer change turns into a breaking wire change later.
/// </summary>
public class MessageSummary
{
    public string Id { get; set; } = null!;

    /// <summary>The stored timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public string AuthorId { get; set; } = null!;
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public byte[] Content { get; set; } = [];
    public string? ComponentsJson { get; set; }
    public string? EmbedsJson { get; set; }
}

public class GetMessageResponse
{
    /// <summary>Null when no message with that id exists.</summary>
    public MessageSummary? Message { get; set; }
}
