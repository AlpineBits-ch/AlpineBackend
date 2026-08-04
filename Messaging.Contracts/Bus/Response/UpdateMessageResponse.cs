namespace Messaging.Contracts.Bus.Response;

public class UpdateMessageResponse
{
    public bool Success { get; set; }
    public bool NotFound { get; set; }
    public bool Forbidden { get; set; }

    /// <summary>The stored content no longer matches <c>UpdateMessageCommand.ExpectedContentSha256</c>,
    /// so nothing was written. Not an error: it means the author edited the message while a link
    /// preview was being fetched, and the stale preview was correctly dropped.</summary>
    public bool Stale { get; set; }
    public byte[]? Content { get; set; }
    public string? EmbedsJson { get; set; }
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public string? AuthorId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
