namespace Messaging.Contracts.Bus.Request;

/// <summary>Fetches the newest message left in a channel, for callers that denormalize its head.</summary>
public class GetChannelHeadRequest
{
    public required string ChannelId { get; init; }
}
