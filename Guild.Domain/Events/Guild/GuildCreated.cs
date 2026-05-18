using Domain;

namespace Guild.Domain.Events.Guild;

public class GuildCreated : DomainEvent
{
    public string GuildId { get; set; }
}