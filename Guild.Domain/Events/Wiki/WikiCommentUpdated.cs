using Domain;

namespace Guild.Domain.Events.Wiki;

public class WikiCommentUpdated : DomainEvent
{
    public string CommentId { get; set; }
    public string PageId { get; set; }
    public string GuildId { get; set; }
}
