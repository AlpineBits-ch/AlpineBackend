using Domain;

namespace Guild.Domain.Events.Permission;

public class ChannelPermissionChanged : DomainEvent
{
    public string GuildId { get; set; }
    public string? RoleId { get; set; }
    public string? MemberId { get; set; }
}
