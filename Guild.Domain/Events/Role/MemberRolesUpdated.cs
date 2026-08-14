using Domain;

namespace Guild.Domain.Events.Role;

/// <summary>
/// Raised by the bulk member-role edit: one event for a whole set of adds and removes.
/// </summary>
public class MemberRolesUpdated : DomainEvent
{
    public string GuildId { get; set; } = string.Empty;

    /// <summary><c>GuildMember.Id</c>, not the auth user id; the handler resolves the latter.</summary>
    public string MemberId { get; set; } = string.Empty;

    public List<string> AddedRoleIds { get; set; } = [];
    public List<string> RemovedRoleIds { get; set; } = [];
}
