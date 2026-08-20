using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Permission;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// Keeps <see cref="Channel.IsPrivate"/> and the channel's @everyone ViewChannel overwrite saying
/// the same thing. The flag was display-only until this existed: nothing in
/// <see cref="GuildPermissionService"/> read it, so a channel a client had marked private stayed
/// readable by the whole guild.
/// </summary>
public class ChannelPrivacyService(MicroserviceContext ctx)
{
    /// <summary>
    /// Writes <paramref name="isPrivate"/> onto the channel and syncs the @everyone overwrite to
    /// match.
    /// </summary>
    /// <param name="channel">The channel being created or updated, already tracked.</param>
    /// <param name="isPrivate">The requested state, or null to leave both untouched.</param>
    /// <returns>The invalidation event to publish, or null when nothing changed.</returns>
    public async Task<ChannelPermissionChanged?> ApplyAsync(Channel channel, bool? isPrivate)
    {
        if (isPrivate is not { } wanted) return null;

        var everyoneRoleId = await ctx.Roles
            .AsNoTracking()
            .Where(r => r.GuildId == channel.GuildId && r.Type == RoleType.Everyone)
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        // A guild with no @everyone role has nothing to deny, so the flag would be a claim the
        // permission model cannot back. Refuse to record it rather than record it falsely.
        if (everyoneRoleId is null) return null;

        var existing = await ctx.Set<ChannelPermission>()
            .FirstOrDefaultAsync(p =>
                p.ChannelId == channel.Id && p.CategoryId == null &&
                p.RoleId == everyoneRoleId && p.MemberId == null);

        var deny = existing?.DenyPermissions ?? Permissions.None;
        var allow = existing?.AllowPermissions ?? Permissions.None;

        var newDeny = wanted ? deny | Permissions.ViewChannel : deny & ~Permissions.ViewChannel;

        // A private channel cannot also carry an @everyone allow of the bit it denies, and the
        // resolution order applies allow last - so leaving it would undo the deny outright.
        var newAllow = wanted ? allow & ~Permissions.ViewChannel : allow;

        channel.IsPrivate = wanted;

        if (newDeny == deny && newAllow == allow) return null;

        if (existing is not null) ctx.Set<ChannelPermission>().Remove(existing);

        // Nothing left to say once ViewChannel is out of both masks - a row of all-None would only
        // slow the resolver down.
        var stillMeaningful = newDeny != Permissions.None || newAllow != Permissions.None ||
                              (existing?.AllowModulePermissions ?? ModulePermissions.None) != ModulePermissions.None ||
                              (existing?.DenyModulePermissions ?? ModulePermissions.None) != ModulePermissions.None;

        if (stillMeaningful)
        {
            var now = DateTimeOffset.UtcNow;

            // AllowPermissions/DenyPermissions are init-only, so a change is remove + re-add.
            ctx.Set<ChannelPermission>().Add(new ChannelPermission
            {
                Id = ChannelPermission.GenerateId(),
                ChannelId = channel.Id,
                CategoryId = null,
                RoleId = everyoneRoleId,
                MemberId = null,
                AllowPermissions = newAllow,
                DenyPermissions = newDeny,
                AllowModulePermissions = existing?.AllowModulePermissions ?? ModulePermissions.None,
                DenyModulePermissions = existing?.DenyModulePermissions ?? ModulePermissions.None,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now,
            });
        }

        return new ChannelPermissionChanged { GuildId = channel.GuildId, RoleId = everyoneRoleId };
    }

    /// <summary>
    /// The reverse direction: re-derives <see cref="Channel.IsPrivate"/> from the @everyone
    /// overwrite, so editing that overwrite directly cannot leave the flag lying.
    /// </summary>
    /// <param name="channelId">The channel whose overwrite just changed.</param>
    /// <param name="roleId">The role the changed overwrite targets.</param>
    public async Task SyncFlagFromOverwriteAsync(string? channelId, string? roleId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(roleId)) return;

        var guildId = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => c.GuildId)
            .FirstOrDefaultAsync();

        if (guildId is null) return;

        var isEveryone = await ctx.Roles
            .AsNoTracking()
            .AnyAsync(r => r.Id == roleId && r.GuildId == guildId && r.Type == RoleType.Everyone);

        if (isEveryone) await SyncFlagAsync(channelId);
    }

    /// <summary>
    /// Re-reads the flag off whatever @everyone overwrite the channel currently carries, for
    /// callers that rewrote the overwrites wholesale rather than one role at a time.
    /// </summary>
    /// <param name="channelId">The channel to bring back into agreement with itself.</param>
    public async Task SyncFlagAsync(string channelId)
    {
        var channel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return;

        // The filters in BuildDenyPermissionsQuery match at most one row.
        var denyPermissions = await BuildDenyPermissionsQuery(ctx, channelId, channel.GuildId)
            .FirstOrDefaultAsync();

        var denied = denyPermissions is { } deny && (deny & Permissions.ViewChannel) == Permissions.ViewChannel;

        if (channel.IsPrivate != denied) channel.IsPrivate = denied;
    }

    /// <summary>
    /// The @everyone channel overwrite's deny mask for <paramref name="channelId"/>, or null when
    /// there is no such row. Postgres has no <c>&amp;</c> operator for numeric, which is what a
    /// ulong-backed <see cref="Permissions"/> column maps to, so callers must bit-test the result
    /// in memory rather than push the comparison into this query.
    /// </summary>
    /// <param name="ctx">The database context.</param>
    /// <param name="channelId">The channel the overwrite belongs to.</param>
    /// <param name="guildId">The channel's guild, to identify its @everyone role.</param>
    public static IQueryable<Permissions?> BuildDenyPermissionsQuery(
        MicroserviceContext ctx, string channelId, string guildId) =>
        ctx.Set<ChannelPermission>()
            .AsNoTracking()
            .Where(p => p.ChannelId == channelId && p.CategoryId == null && p.MemberId == null &&
                        p.Role.GuildId == guildId && p.Role.Type == RoleType.Everyone)
            .Select(p => (Permissions?)p.DenyPermissions);

    /// <summary>
    /// The in-memory twin of <see cref="SyncFlagAsync"/>: derives <see cref="Channel.IsPrivate"/>
    /// from a set of rows already held by the caller instead of a fresh query, for a caller that
    /// just built those rows in the same unit of work and cannot see them again before it commits.
    /// </summary>
    /// <param name="channel">The channel to bring into agreement with itself, already tracked.</param>
    /// <param name="rows">The channel's full overwrite set after whatever change just happened.</param>
    /// <param name="everyoneRoleId">The guild's @everyone role id.</param>
    public void SyncFlagFrom(Channel channel, IEnumerable<ChannelPermission> rows, string everyoneRoleId)
    {
        var denied = rows.Any(p => p.CategoryId == null && p.MemberId == null && p.RoleId == everyoneRoleId &&
                                    (p.DenyPermissions & Permissions.ViewChannel) == Permissions.ViewChannel);

        if (channel.IsPrivate != denied) channel.IsPrivate = denied;
    }
}
