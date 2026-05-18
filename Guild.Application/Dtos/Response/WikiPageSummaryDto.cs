using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(WikiPage), nameof(WikiPage.Content), nameof(WikiPage.Revisions))]
public partial class WikiPageSummaryDto
{
    public int RevisionCount { get; set; }
}
