using Domain;

namespace Guild.Domain.Events.Channel;

public class ChannelUpdated : DomainEvent
{
    public string ChannelId { get; set; }
    public string GuildId { get; set; }   
}