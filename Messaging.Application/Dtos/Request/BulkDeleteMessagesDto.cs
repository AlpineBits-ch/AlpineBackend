namespace Messaging.Application.Dtos.Request;

public class BulkDeleteMessagesDto
{
    /// <summary>The channel every id must belong to.</summary>
    public string ChannelId { get; set; } = null!;

    public List<string> MessageIds { get; set; } = [];
}
