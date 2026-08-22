namespace Social.Contracts.Bus.Integration.Request;

/// <summary>One page of the catalog, as topics: names and aliases, no executable rules.</summary>
public class ListGameTopicsRequest
{
    public int Limit { get; set; } = 500;

    /// <summary>The last id of the previous page. Null starts over.</summary>
    public string? After { get; set; }
}

public class ListGameTopicsResponse
{
    public IReadOnlyList<GameTopicDto> Topics { get; set; } = [];
    public string? NextCursor { get; set; }
}

public class GameTopicDto
{
    public string Id { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string[] Aliases { get; set; } = [];
    public string? SteamAppId { get; set; }
    public bool IsEnabled { get; set; }
}
