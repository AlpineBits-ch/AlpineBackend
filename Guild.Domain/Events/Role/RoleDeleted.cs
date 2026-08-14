using Domain;

namespace Guild.Domain.Events.Role;

/// <summary>Raised after a role row is gone.</summary>
public class RoleDeleted : DomainEvent
{
    public string RoleId { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;

    /// <summary>Auth user ids of everyone who held the role at the moment it was deleted.</summary>
    public List<string> UserIds { get; set; } = [];
}
