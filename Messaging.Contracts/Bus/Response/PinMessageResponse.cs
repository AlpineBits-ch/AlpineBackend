namespace Messaging.Contracts.Bus.Response;

public class PinMessageResponse
{
    public bool Success { get; set; }
    public bool NotFound { get; set; }
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public string? AuthorId { get; set; }
    public string? PinnedById { get; set; }
    public DateTime? PinnedAt { get; set; }
}
