namespace Guild.Application.Dtos.Request;

public class RolePositionDto
{
    public string RoleId { get; set; } = string.Empty;

    /// <summary>The role's new position.</summary>
    public int Position { get; set; }
}

/// <summary>The body of <c>PATCH /api/v1/guilds/{guildId}/roles/reorder</c>.</summary>
public class ReorderRolesDto
{
    public List<RolePositionDto> Roles { get; set; } = [];
}

/// <summary>
/// The body of <c>PATCH /api/v1/guilds/{guildId}/members/{memberId}/roles</c>: the complete set of
/// roles the member should hold when the call returns.
/// </summary>
public class SetMemberRolesDto
{
    public List<string> RoleIds { get; set; } = [];
}
