namespace Discovery.Api.Dtos.Response;

/// <summary>One feed row: a published listing plus display-only guild identity from the mirror.
/// Deliberately not ListingDto - the feed never carries drafts, links or suspension state, and one
/// type for both would invite a component to read a field that is always undefined here.</summary>
public class DiscoveryCardDto
{
    public required string ListingId { get; init; }
    public required string GuildId { get; init; }
    public required string GuildName { get; init; }
    public string? GuildIconUrl { get; init; }
    public string? GuildBannerUrl { get; init; }
    public required int MemberCount { get; init; }
    public required string Headline { get; init; }
    public required string Pitch { get; init; }
    public required string Language { get; init; }
    public required string JoinPolicy { get; init; }

    /// <summary>Why this card surfaced. Spec section 9.2: a feed that cannot say why it surfaced
    /// something is a feed people stop trusting.</summary>
    public required IReadOnlyList<TopicDto> MatchedTopics { get; init; }
}
