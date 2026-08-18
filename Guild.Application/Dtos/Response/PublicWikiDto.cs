namespace Guild.Application.Dtos.Response;

/// <summary>
/// A published wiki as an anonymous reader sees it. Hand-written rather than a Facet over
/// <see cref="Guild.Domain.Entity.Wiki"/> on purpose: a Facet is the whole entity minus an exclusion
/// list, so every column added later would join the public wire shape by default. Everything here
/// was chosen.
/// </summary>
public class PublicWikiDto
{
    /// <summary>The slug this wiki answers on.</summary>
    public required string Slug { get; init; }

    /// <summary>The publishing guild's name, which is the guild identifying itself.</summary>
    public required string GuildName { get; init; }

    /// <summary>The guild's own description, when it has one.</summary>
    public string? GuildDescription { get; init; }

    /// <summary>The published pages, alphabetically.</summary>
    public List<PublicWikiPageSummaryDto> Pages { get; init; } = [];
}

/// <summary>One published page in the index.</summary>
public class PublicWikiPageSummaryDto
{
    public required string Slug { get; init; }
    public required string Title { get; init; }

    /// <summary>A single emoji, or null.</summary>
    public string? Icon { get; init; }

    /// <summary>The name of the category the page sits in, or null.</summary>
    public string? Category { get; init; }
}

/// <summary>
/// One published page in full. Carries no author, no editor, no guild id, no visibility, no tags and
/// no engagement counts - see <see cref="PublicWikiDto"/> for why the shape is written out.
/// </summary>
public class PublicWikiPageDto
{
    public required string Slug { get; init; }
    public required string Title { get; init; }

    /// <summary>The page body, as the markdown the author wrote. Rendering and sanitisation happen
    /// at the gateway, which is the only thing that turns this into HTML.</summary>
    public required string Content { get; init; }

    public string? Icon { get; init; }

    /// <summary>An instance-hosted cover image, or null. Off-instance covers are refused at publish
    /// time rather than filtered here.</summary>
    public string? CoverUrl { get; init; }

    public string? Category { get; init; }

    /// <summary>When the page was last edited, to the page rather than to any person. Revision
    /// history stays off this surface entirely.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The publishing guild's name and slug, so a page can link back to its index.</summary>
    public required string GuildName { get; init; }

    public required string WikiSlug { get; init; }
}
