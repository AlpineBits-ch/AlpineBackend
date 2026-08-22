namespace Discovery.Api.Dtos.Response;

/// <summary>One page of the ranked feed. NextCursor is null on the last page.</summary>
public class DiscoveryFeedDto
{
    public required IReadOnlyList<DiscoveryCardDto> Cards { get; init; }
    public string? NextCursor { get; init; }
}
