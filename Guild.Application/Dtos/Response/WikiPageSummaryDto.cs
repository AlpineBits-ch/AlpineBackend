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
}
