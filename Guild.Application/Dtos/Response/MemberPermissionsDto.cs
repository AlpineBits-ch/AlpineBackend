using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

/// <summary>
/// The four guild-level override masks stored on a member row, as returned by <c>PATCH
/// /api/v1/guilds/{guildId}/members/{memberId}/permissions</c>.
/// </summary>
public sealed record MemberPermissionsDto
{
    public required Permissions AllowPermissions { get; init; }
    public required Permissions DenyPermissions { get; init; }
    public required ModulePermissions AllowModulePermissions { get; init; }
    public required ModulePermissions DenyModulePermissions { get; init; }
}
