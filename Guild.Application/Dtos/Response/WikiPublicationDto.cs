namespace Guild.Application.Dtos.Response;

/// <summary>A guild's public-wiki state, and whether it currently does anything.</summary>
public class WikiPublicationDto
{
    public required string GuildId { get; init; }

    /// <summary>The slug the wiki is published on, lowercase, or null if it is not published.</summary>
    public string? Slug { get; init; }

    /// <summary>Whether the guild's plan currently covers public hosting.</summary>
    public bool Entitled { get; init; }

    /// <summary>Whether anonymous readers can reach this wiki right now.</summary>
    public bool Active { get; init; }

    /// <summary>How many pages carry the per-page opt-in.</summary>
    public int PublishedPageCount { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>Whether one page is on the public host.</summary>
public class WikiPagePublicationDto
{
    public required string PageId { get; init; }

    /// <summary>Whether the page carries the per-page opt-in.</summary>
    public bool Published { get; init; }

    /// <summary>Whether the page is actually reachable, which also needs the wiki to be published.</summary>
    public bool Active { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>The public URL, when the page is reachable.</summary>
    public string? Url { get; init; }
}
