namespace Messaging.Contracts.Bus.Response;

public class UpdateMessageResponse
{
    public bool Success { get; set; }
    public bool NotFound { get; set; }
    public bool Forbidden { get; set; }
    public byte[]? Content { get; set; }
    public string? EmbedsJson { get; set; }
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public string? AuthorId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
