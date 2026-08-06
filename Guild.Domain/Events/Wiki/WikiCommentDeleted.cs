using Domain;

namespace Guild.Domain.Events.Wiki;

public class WikiCommentDeleted : DomainEvent
{
    public string CommentId { get; set; }
    public string PageId { get; set; }
    public string GuildId { get; set; }
}
