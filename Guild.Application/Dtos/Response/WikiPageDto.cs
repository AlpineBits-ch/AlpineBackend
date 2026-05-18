using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(WikiPage), nameof(WikiPage.Revisions))]
public partial class WikiPageDto
{
    public int RevisionCount { get; set; }
}
