using Domain;

namespace Guild.Domain.Events.Role;

public class RoleUpdated : DomainEvent
{
    public string RoleId { get; set; }
    public string GuildId { get; set; }
    public string? MemberId { get; set; }
}