using Domain;

namespace Guild.Domain.Events.Wiki;

public class WikiPageReactionRemoved : DomainEvent
{
    public string PageId { get; set; }
    public string GuildId { get; set; }
    public string UserId { get; set; }
    public string Emoji { get; set; }
}
