using Domain;

namespace Guild.Domain.Events.Wiki;

public class WikiCommentCreated : DomainEvent
{
    public string CommentId { get; set; }
    public string PageId { get; set; }
    public string GuildId { get; set; }
    public string AuthorId { get; set; }
}
