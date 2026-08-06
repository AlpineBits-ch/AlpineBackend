using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(WikiPage), nameof(WikiPage.Revisions))]
public partial class WikiPageDto
{
    public int RevisionCount { get; set; }

    /// <summary>Aggregated emoji reactions, ordered by count descending then emoji, with
    /// <see cref="WikiReactionDto.Me"/> answering "did I react" for the calling user. Empty rather
    /// than null when nobody has reacted.</summary>
    public List<WikiReactionDto> Reactions { get; set; } = [];
}
