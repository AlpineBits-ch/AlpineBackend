namespace Discovery.Api.Dtos.Response;

/// <summary>A resolved topic: a game from the mirrored catalog, or a free-form tag.</summary>
public class TopicDto
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? SteamAppId { get; init; }
}
