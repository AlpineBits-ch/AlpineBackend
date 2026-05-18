using Domain;

namespace Guild.Domain.Events.Wiki;

public class WikiCategoryCreated : DomainEvent
{
    public string CategoryId { get; set; }
    public string GuildId { get; set; }
    public string? ParentCategoryId { get; set; }
}
