namespace Messaging.Contracts.Bus.Request;

/// <summary>One channel's slice of an inbox page: everything after the caller's read cursor.</summary>
public class ChannelMessagePageQuery
{
    public required string ChannelId { get; init; }

    /// <summary>The caller's read cursor.</summary>
    public string? AfterMessageId { get; init; }
}

/// <summary>Fetches unread previews for several channels in one round trip.</summary>
public class GetChannelMessagePagesRequest
{
    /// <summary>Hard cap on channels per request.</summary>
    public const int MaxChannels = 25;

    /// <summary>Hard cap on messages returned per channel.</summary>
    public const int MaxMessagesPerChannel = 10;

    public required IReadOnlyList<ChannelMessagePageQuery> Items { get; init; }

    public int MessagesPerChannel { get; init; } = 5;
}
