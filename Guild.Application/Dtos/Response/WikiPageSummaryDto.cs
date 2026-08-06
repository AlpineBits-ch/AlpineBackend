using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(WikiPage), nameof(WikiPage.Content), nameof(WikiPage.Revisions))]
public partial class WikiPageSummaryDto
{
    public int RevisionCount { get; set; }

    /// <summary>
    /// Populated only when the wiki is fetched with <c>?includeContent=true</c>, which clients
    /// doing full-text search or backlink indexing need.
    /// </summary>
    public string? Content { get; set; }

    /// <summary>Total reactions on the page across all emoji - a badge number for the tree, not the
    /// per-emoji breakdown. Fetch the page itself for that.</summary>
    public int ReactionCount { get; set; }

    /// <summary>How many comments the page has.</summary>
    public int CommentCount { get; set; }

    /// <summary>Whether the calling user is watching this page.</summary>
    public bool IsWatching { get; set; }
}
