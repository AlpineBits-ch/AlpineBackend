namespace Messaging.Contracts.Bus.Response;

/// <summary>The newest message a channel still holds.</summary>
public class GetChannelHeadResponse
{
    /// <summary>Null when the channel has no messages left.</summary>
    public string? MessageId { get; set; }

    /// <summary>The stored timestamp, null when the channel has no messages left.</summary>
    public DateTimeOffset? CreatedAt { get; set; }
}
