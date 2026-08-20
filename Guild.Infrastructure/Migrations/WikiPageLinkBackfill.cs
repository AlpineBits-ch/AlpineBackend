namespace Guild.Persistence.Migrations;

/// <summary>
/// Fills the link graph for pages written before it existed. SQL cannot run the markdown parser the
/// write path uses, so this is a regex over the stored body and it also matches a wiki: link inside
/// a code fence, which the parser would have skipped. Each page corrects itself on its next save.
/// </summary>
public static class WikiPageLinkBackfill
{
    /// <summary>
    /// Inserts one row per (page, target) found in <c>wiki_pages.content</c>, skipping self-links.
    /// </summary>
    public const string ExtractExistingLinksSql = """
        INSERT INTO wiki_page_links (source_page_id, target_page_id, guild_id, heading_id)
        SELECT DISTINCT ON (source_page_id, target_page_id)
               source_page_id, target_page_id, guild_id, heading_id
        FROM (
          SELECT p.id AS source_page_id,
                 m.captures[1] AS target_page_id,
                 p.guild_id AS guild_id,
                 nullif(m.captures[2], '') AS heading_id
          FROM wiki_pages p
          CROSS JOIN LATERAL regexp_matches(
            p.content, '\]\(wiki:([A-Za-z0-9_-]+)(?:#([^)\s]*))?\)', 'g') AS m(captures)
        ) links
        WHERE target_page_id <> source_page_id
        ORDER BY source_page_id, target_page_id
        ON CONFLICT DO NOTHING;
        """;
}
