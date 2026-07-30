namespace Messaging.Application.Dtos.Request;

public class BulkDeleteMessagesDto
{
    /// <summary>The channel every id must belong to. Required rather than inferred from the
    /// messages themselves: the permission check has to happen against a known channel *before*
    /// anything is read, and accepting a mixed-channel batch would mean one check per message.</summary>
    public string ChannelId { get; set; } = null!;

    public List<string> MessageIds { get; set; } = [];
}
