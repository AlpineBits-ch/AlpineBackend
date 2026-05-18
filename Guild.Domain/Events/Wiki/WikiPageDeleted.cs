using Domain;

namespace Guild.Domain.Events.Wiki;

public class WikiPageDeleted : DomainEvent
{
    public string PageId { get; set; }
    public string GuildId { get; set; }
}
