using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// Who in a guild holds a module permission. Events addressed to a job rather than to the room
/// need this: an approval queue reaches its reviewers, a stale turn reaches the GM, and a
/// reviewer's reason or a character's grants have no business on a guild-wide broadcast.
/// </summary>
public class ModulePermissionHolderService(
    MicroserviceContext ctx,
    GuildPermissionService permissions)
{
    /// <summary>
    /// Everyone who can currently exercise a module permission in a guild: the owner, plus whoever
    /// holds it through a role. A member-level allow overwrite is deliberately not searched for,
    /// because finding one means reading every member row of the guild to serve a case nothing
    /// sets in bulk.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="required">The permission the recipients need.</param>
    /// <returns>The holders' user ids.</returns>
    public async Task<List<string>> HoldersAsync(string guildId, ModulePermissions required)
    {
        var ownerId = await ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == guildId)
            .Select(g => g.OwnerId)
            .FirstOrDefaultAsync();

        // The mask is a [Flags] ulong stored as numeric, so the bit test happens here rather than in
        // SQL; a guild has tens of roles, not thousands.
        var roles = await ctx.Roles
            .AsNoTracking()
            .Where(r => r.GuildId == guildId)
            .Select(r => new { r.Id, r.ModulePermissions })
            .ToListAsync();

        var roleIds = roles
            .Where(r => (r.ModulePermissions & required) == required)
            .Select(r => r.Id)
            .ToList();

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(ownerId)) candidates.Add(ownerId);

        if (roleIds.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var fromRoles = await ctx.RoleMembers
                .AsNoTracking()
                .Where(rm => roleIds.Contains(rm.RoleId) && (rm.ExpiresAt == null || rm.ExpiresAt > now))
                .Join(ctx.GuildMembers.AsNoTracking().Where(m => m.GuildId == guildId),
                    rm => rm.MemberId, m => m.Id, (_, m) => m.UserId)
                .Distinct()
                .ToListAsync();

            candidates.UnionWith(fromRoles);
        }

        var holders = new List<string>(candidates.Count);
        foreach (var userId in candidates)
        {
            // Confirmed rather than inferred: the role grant says nothing about a member-level deny
            // or about the module being switched off since.
            if (await permissions.CanUserPerformActionOnGuildAsync(userId, guildId, required))
                holders.Add(userId);
        }

        return holders;
    }
}
