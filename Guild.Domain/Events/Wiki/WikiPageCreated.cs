using Domain;

namespace Guild.Domain.Events.Wiki;

public class WikiPageCreated : DomainEvent
{
    public string PageId { get; set; }
    public string GuildId { get; set; }
}
