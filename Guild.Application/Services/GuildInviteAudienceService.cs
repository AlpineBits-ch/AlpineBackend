using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// Who may be told about an invite in realtime: the online members of a guild who hold <see
/// cref="Permissions.ManageGuild"/>.
/// </summary>
public class GuildInviteAudienceService(MicroserviceContext ctx, GuildHydrateService presenceSource)
{
    public async Task<IReadOnlyList<string>> ResolveAsync(string guildId)
    {
        var presence = await presenceSource.GetGuildPresenceAsync(guildId);

        var online = presence.Select(p => p.UserId).Distinct(StringComparer.Ordinal).ToList();
        if (online.Count == 0) return [];

        var ownerId = await ctx.Guilds.AsNoTracking()
            .Where(g => g.Id == guildId)
            .Select(g => g.OwnerId)
            .FirstOrDefaultAsync();

        var members = await ctx.GuildMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && online.Contains(m.UserId))
            .Select(m => new { m.Id, m.UserId, m.AllowPermissions, m.DenyPermissions })
            .ToListAsync();

        if (members.Count == 0) return Owner(ownerId, online);

        var memberIds = members.Select(m => m.Id).ToList();

        var roleAssignments = await ctx.RoleMembers.AsNoTracking()
            .Where(rm => memberIds.Contains(rm.MemberId))
            .Select(rm => new { rm.MemberId, rm.RoleId })
            .ToListAsync();

        // The whole guild's roles rather than only the assigned ones: a guild's role list is small
        // and capped, and loading it whole keeps this to a fixed number of round trips regardless of
        // how many people are online.
        var rolePermissions = await ctx.Roles.AsNoTracking()
            .Where(r => r.GuildId == guildId)
            .Select(r => new { r.Id, r.Permissions })
            .ToListAsync();

        var permissionByRole = rolePermissions.ToDictionary(r => r.Id, r => r.Permissions, StringComparer.Ordinal);

        var rolesByMember = roleAssignments
            .GroupBy(rm => rm.MemberId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.RoleId).ToList(), StringComparer.Ordinal);

        var audience = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(ownerId) && online.Contains(ownerId, StringComparer.Ordinal))
            audience.Add(ownerId);

        foreach (var member in members)
        {
            var resolved = Permissions.None;

            if (rolesByMember.TryGetValue(member.Id, out var roleIds))
            {
                foreach (var roleId in roleIds)
                {
                    if (permissionByRole.TryGetValue(roleId, out var mask)) resolved |= mask;
                }
            }

            resolved = (resolved | member.AllowPermissions) & ~member.DenyPermissions;

            if (resolved.HasPermission(Permissions.ManageGuild)) audience.Add(member.UserId);
        }

        return audience.ToList();
    }

    private static IReadOnlyList<string> Owner(string? ownerId, List<string> online) =>
        !string.IsNullOrWhiteSpace(ownerId) && online.Contains(ownerId, StringComparer.Ordinal)
            ? [ownerId]
            : [];
}
