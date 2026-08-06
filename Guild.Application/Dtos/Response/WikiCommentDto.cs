using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(WikiComment))]
public partial class WikiCommentDto
{
    /// <summary>Whether the calling user may delete this comment - true for their own comment, and
    /// true for anyone holding ModerateWikiComments. Saves the client re-deriving a permission
    /// decision the server has already made.</summary>
    public bool CanDelete { get; set; }
}
