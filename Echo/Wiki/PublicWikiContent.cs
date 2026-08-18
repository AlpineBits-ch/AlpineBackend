namespace Echo.Wiki;

/// <summary>A published wiki's index, as the Guild service hands it over.</summary>
/// <param name="Slug">The slug the wiki answers on.</param>
/// <param name="GuildName">The publishing guild's name.</param>
/// <param name="GuildDescription">The publishing guild's description, when it has one.</param>
/// <param name="Pages">The published pages.</param>
public sealed record PublicWiki(
    string Slug,
    string GuildName,
    string? GuildDescription,
    IReadOnlyList<PublicWikiPageSummary> Pages);

/// <summary>One published page in the index.</summary>
/// <param name="Slug">The page's slug.</param>
/// <param name="Title">The page's title.</param>
/// <param name="Icon">A single emoji, or null.</param>
/// <param name="Category">The category name, or null.</param>
public sealed record PublicWikiPageSummary(string Slug, string Title, string? Icon, string? Category);

/// <summary>
/// One published page in full. There is deliberately no author, no editor, no guild id and no
/// engagement count anywhere in this shape - see the DTO in Guild.Application for the reasoning.
/// </summary>
/// <param name="Slug">The page's slug.</param>
/// <param name="Title">The page's title.</param>
/// <param name="Content">The page body as markdown.</param>
/// <param name="Icon">A single emoji, or null.</param>
/// <param name="CoverUrl">An instance-hosted cover image, or null.</param>
/// <param name="Category">The category name, or null.</param>
/// <param name="UpdatedAt">When the page was last edited.</param>
/// <param name="GuildName">The publishing guild's name.</param>
/// <param name="WikiSlug">The slug of the wiki this page belongs to.</param>
public sealed record PublicWikiPage(
    string Slug,
    string Title,
    string Content,
    string? Icon,
    string? CoverUrl,
    string? Category,
    DateTimeOffset UpdatedAt,
    string GuildName,
    string WikiSlug);
