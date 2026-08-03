namespace Messaging.Contracts.Bus.Request;

/// <summary>One channel's slice of an inbox page: everything after the caller's read cursor.</summary>
public class ChannelMessagePageQuery
{
    public required string ChannelId { get; init; }

    /// <summary>The caller's read cursor. Null when they have never opened the channel, in which
    /// case the oldest messages are returned instead of a cursor page.</summary>
    public string? AfterMessageId { get; init; }
}

/// <summary>
/// Fetches unread previews for several channels in one round trip.
///
/// <para>Batched rather than one request per channel because the inbox renders a page of channel
/// groups at once - the alternative is a bus round trip per group, per open. Each item is still a
/// single-partition read on the Messaging side, so the batch costs what its widest item costs, not
/// the sum.</para>
///
/// <para>Deliberately capped: this is a fan-out request, and an uncapped one is a way to turn a
/// single inbox call into arbitrary load on Messaging.</para>
/// </summary>
public class GetChannelMessagePagesRequest
{
    /// <summary>Hard cap on channels per request. Matches the inbox's own page size - a caller
    /// wanting more pages the inbox, not this.</summary>
    public const int MaxChannels = 25;

    /// <summary>Hard cap on messages returned per channel. The inbox shows a handful under each
    /// group, not the whole backlog.</summary>
    public const int MaxMessagesPerChannel = 10;

    public required IReadOnlyList<ChannelMessagePageQuery> Items { get; init; }

    public int MessagesPerChannel { get; init; } = 5;
}
