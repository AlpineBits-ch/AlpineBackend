namespace Guild.Domain.Entity;

/// <summary>
/// One page's link to another, derived from the source page's markdown and rewritten whenever that
/// body is saved. Keyless composite join in the shape of WikiPageWatcher: a single edge is not worth
/// addressing on its own.
/// </summary>
public class WikiPageLink
{
    public string SourcePageId { get; set; }

    /// <summary>The linked page, which need not exist: an unwritten target is a red link.</summary>
    public string TargetPageId { get; set; }

    /// <summary>Denormalised from the source page so the graph query never joins back to
    /// wiki_pages.</summary>
    public string GuildId { get; set; }

    /// <summary>The heading slug after the '#', or null when the link points at the page.</summary>
    public string? HeadingId { get; set; }
}
