using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

/// <summary>
/// The body of <c>PATCH /api/v1/guilds/{guildId}/members/{memberId}/permissions</c> - the
/// guild-level allow/deny overrides stored on the member row itself, applied after role aggregation
/// and before channel/category overwrites (see <c>GuildMember.AllowPermissions</c>).
/// </summary>
public class SetMemberPermissionsDto
{
    /// <inheritdoc cref="SetMemberPermissionsDto"/>
    public Permissions? AllowPermissions { get; set; }

    /// <inheritdoc cref="SetMemberPermissionsDto"/>
    public Permissions? DenyPermissions { get; set; }

    /// <inheritdoc cref="SetMemberPermissionsDto"/>
    public ModulePermissions? AllowModulePermissions { get; set; }

    /// <inheritdoc cref="SetMemberPermissionsDto"/>
    public ModulePermissions? DenyModulePermissions { get; set; }
}
