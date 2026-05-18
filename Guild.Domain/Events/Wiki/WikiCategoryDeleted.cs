using Domain;

namespace Guild.Domain.Events.Wiki;

public class WikiCategoryDeleted : DomainEvent
{
    public string CategoryId { get; set; }
    public string GuildId { get; set; }
}
