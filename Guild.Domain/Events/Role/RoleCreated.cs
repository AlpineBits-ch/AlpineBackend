using Domain;

namespace Guild.Domain.Events.Role;

public class RoleCreated : DomainEvent
{
    public string RoleId { get; set; }
    public string GuildId { get; set; } 
}