using System.ComponentModel.DataAnnotations.Schema;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class ChannelPermission : BaseEntity<ChannelPermission>, IPrefixedEntity
{
    public string? ChannelId { get; init; }
    public Channel Channel { get; init; }
    public string? RoleId { get; init; }
    public Role Role { get; init; }
    public string? MemberId { get; init; }
    public GuildMember? Member { get; init; }

    public Category? Category { get; init; }
    public string? CategoryId { get; init; }
    public Permissions AllowPermissions { get; init; }
    public Permissions DenyPermissions { get; init; }

    public PermissionState GetState(Permissions perm)
    {
        if (AllowPermissions.HasFlag(perm)) return PermissionState.Allow;
        if (DenyPermissions.HasFlag(perm)) return PermissionState.Deny;
        return PermissionState.Inherit;
    }

    [NotMapped] public static string Prefix { get; } = "chpr";
}
