using System.Numerics;
using System.Security.Claims;
using System.Text.Json;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Guild.Application.Services;

/// <summary>The version token embedded in every permission cache key.</summary>
internal static class PermissionCacheVersion
{
    public const string Token = "v2";
}

public class GuildChannelPermission
{
    public string UserId { get; set; }
    public string ChannelId { get; set; }
    public string GuildId { get; set; }
    public Permissions Permissions { get; set; }
    public ModulePermissions ModulePermissions { get; set; }

    public static string GetCacheKey(string guildId, string channelId, string userId)
    {
        var g = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(guildId));
        var c = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(channelId));
        var u = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(userId));
        return $"guild:{PermissionCacheVersion.Token}:{g}:channel:{c}:user:{u}";
    }

    public string GetCacheKey() => GetCacheKey(GuildId, ChannelId, UserId);
}

public class GuildPermissionsForUser
{
    public string UserId { get; set; }
    public string GuildId { get; set; }
    public Permissions BasePermissions { get; set; }
    public ModulePermissions BaseModulePermissions { get; set; }
    public ICollection<GuildChannelPermission> Permissions { get; set; }

    public static string GetCacheKey(string guildId, string userId)
    {
        var g = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(guildId));
        var u = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(userId));
        return $"guild:{PermissionCacheVersion.Token}:{g}:user:{u}";
    }

    public string GetCacheKey() => GetCacheKey(GuildId, UserId);
}

public class GuildPermissionService(
    IDistributedCache cache,
    MicroserviceContext ctx,
    ILogger<GuildPermissionService> logger)
{
    private async Task<string?> ResolveGuildIdAsync(string channelId)
    {
        return await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => c.GuildId)
            .FirstOrDefaultAsync();
    }

    private static string FeaturesCacheKey(string guildId)
    {
        var g = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(guildId));
        return $"guild:{PermissionCacheVersion.Token}:{g}:features";
    }

    /// <summary>The guild's enabled modules.</summary>
    public async Task<GuildFeatures> GetGuildFeaturesAsync(string guildId)
    {
        var cacheKey = FeaturesCacheKey(guildId);
        var cached = await cache.GetStringAsync(cacheKey);
        if (!string.IsNullOrWhiteSpace(cached) && ulong.TryParse(cached, out var parsed))
            return (GuildFeatures)parsed;

        var features = await ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == guildId)
            .Select(g => (GuildFeatures?)g.Features)
            .FirstOrDefaultAsync();

        // A missing guild resolves to no modules, which denies every gated permission.
        var resolved = features ?? GuildFeatures.None;

        await cache.SetStringAsync(cacheKey, ((ulong)resolved).ToString(), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15)
        });

        return resolved;
    }

    public async Task<bool> IsFeatureEnabledAsync(string guildId, GuildFeatures feature) =>
        (await GetGuildFeaturesAsync(guildId)).HasFlag(feature);

    public async Task InvalidateGuildFeaturesCacheAsync(string guildId) =>
        await cache.RemoveAsync(FeaturesCacheKey(guildId));

    private static string SlowModeCacheKey(string channelId)
    {
        var c = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(channelId));
        return $"channel:{PermissionCacheVersion.Token}:{c}:slowmode";
    }

    /// <summary>The channel's configured slowmode window in seconds (0 = off).</summary>
    public async Task<int> GetChannelSlowModeSecondsAsync(string channelId)
    {
        var cacheKey = SlowModeCacheKey(channelId);
        var cached = await cache.GetStringAsync(cacheKey);
        if (!string.IsNullOrWhiteSpace(cached) && int.TryParse(cached, out var parsed)) return parsed;

        var seconds = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => (int?)c.SlowModeSeconds)
            .FirstOrDefaultAsync();

        // An unknown channel resolves to "no slowmode" rather than blocking - the caller has
        // already failed the permission check by that point, so this value never reaches a send.
        var resolved = seconds ?? 0;

        await cache.SetStringAsync(cacheKey, resolved.ToString(), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15)
        });

        return resolved;
    }

    public async Task InvalidateChannelSlowModeCacheAsync(string channelId) =>
        await cache.RemoveAsync(SlowModeCacheKey(channelId));

    private async Task<(bool isOwner, List<string> roleIds, string? memberId, Permissions memberAllow, Permissions memberDeny, ModulePermissions memberModuleAllow, ModulePermissions memberModuleDeny, DateTimeOffset? mutedUntil, bool onboardingPending)> GetMembershipAsync(
        string userId, string guildId)
    {
        var isOwner = await ctx.Guilds
            .AsNoTracking()
            .AnyAsync(g => g.Id == guildId && g.OwnerId == userId);

        var memberRow = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.GuildId == guildId)
            .Select(m => new { m.Id, m.AllowPermissions, m.DenyPermissions, m.AllowModulePermissions, m.DenyModulePermissions, m.MutedUntil, m.OnboardingCompletedAt })
            .FirstOrDefaultAsync();

        var memberId = memberRow?.Id;
        var memberAllow = memberRow?.AllowPermissions ?? Permissions.None;
        var memberDeny = memberRow?.DenyPermissions ?? Permissions.None;
        var memberModuleAllow = memberRow?.AllowModulePermissions ?? ModulePermissions.None;
        var memberModuleDeny = memberRow?.DenyModulePermissions ?? ModulePermissions.None;
        var mutedUntil = memberRow?.MutedUntil;

        // A never-accepted member only counts as pending while the guild actually has onboarding
        // switched on.
        var onboardingPending = memberRow is not null
                                && memberRow.OnboardingCompletedAt is null
                                && await ctx.Set<GuildOnboardingConfig>()
                                    .AsNoTracking()
                                    .AnyAsync(c => c.GuildId == guildId && c.Enabled);

        // Expired guest roles are filtered here rather than relying on a sweep having deleted the
        // row, so a lapsed guest loses access at the instant it expires even if cleanup is behind.
        var now = DateTimeOffset.UtcNow;
        var roleIds = memberId == null
            ? []
            : await ctx.RoleMembers
                .AsNoTracking()
                .Where(rm => rm.MemberId == memberId && (rm.ExpiresAt == null || rm.ExpiresAt > now))
                .Select(rm => rm.RoleId)
                .ToListAsync();

        return (isOwner, roleIds, memberId, memberAllow, memberDeny, memberModuleAllow, memberModuleDeny, mutedUntil, onboardingPending);
    }

    public async Task<bool> CanUserPerformActionAsync(
        string userId,
        string channelId,
        Permissions requiredPermission)
    {
        var guildId = await ResolveGuildIdAsync(channelId);

        if (guildId is null)
        {
            logger.LogWarning(
                "Permission check for user {UserId} on channel {ChannelId} failed: channel not found.",
                userId, channelId);
            return false;
        }

        // Ahead of the owner short-circuit on purpose: a module that is switched off is off for
        // everybody, including the owner.
        if (!GuildFeatureMap.IsPermissionAvailable(await GetGuildFeaturesAsync(guildId), requiredPermission))
            return false;

        var (isOwner, _, _, _, _, _, _, _, _) = await GetMembershipAsync(userId, guildId);
        if (isOwner) return true;

        var channelPermission = await ResolveChannelPermissionAsync(userId, guildId, channelId);

        if (channelPermission == null)
        {
            logger.LogWarning(
                "Permission check for user {UserId} on channel {ChannelId} in guild {GuildId} " +
                "found no matching channel in computed permissions.",
                userId, channelId, guildId);
            return false;
        }

        logger.LogDebug("User {UserId} has permissions {Permissions} on channel {ChannelId} in guild {GuildId}", userId, channelPermission.Permissions, channelId, guildId);

        // Threads inherit their parent's resolved permission set (see ComputePermissionsForUserAsync),
        // but "can post" on a thread is governed by SendMessagesInThreads, not SendMessages.
        if (requiredPermission == Permissions.SendMessages)
        {
            var channelType = await ctx.Channels.AsNoTracking().Where(c => c.Id == channelId).Select(c => c.Type).FirstOrDefaultAsync();
            if (channelType == ChannelType.Thread)
                requiredPermission = Permissions.SendMessagesInThreads;
        }

        return (channelPermission.Permissions & requiredPermission) == requiredPermission;
    }

    /// <summary>
    /// The <see cref="ModulePermissions"/> overload of <see
    /// cref="CanUserPerformActionAsync(string,string,Permissions)"/>, in the same order and with
    /// the same fail-closed branches: unknown channel denies, the feature gate runs ahead of the
    /// owner short-circuit, an unresolvable channel permission denies.
    /// </summary>
    public async Task<bool> CanUserPerformActionAsync(
        string userId,
        string channelId,
        ModulePermissions requiredPermission)
    {
        var guildId = await ResolveGuildIdAsync(channelId);

        if (guildId is null)
        {
            logger.LogWarning(
                "Permission check for user {UserId} on channel {ChannelId} failed: channel not found.",
                userId, channelId);
            return false;
        }

        if (!GuildFeatureMap.IsPermissionAvailable(await GetGuildFeaturesAsync(guildId), requiredPermission))
            return false;

        var (isOwner, _, _, _, _, _, _, _, _) = await GetMembershipAsync(userId, guildId);
        if (isOwner) return true;

        var channelPermission = await ResolveChannelPermissionAsync(userId, guildId, channelId);

        if (channelPermission == null)
        {
            logger.LogWarning(
                "Permission check for user {UserId} on channel {ChannelId} in guild {GuildId} " +
                "found no matching channel in computed permissions.",
                userId, channelId, guildId);
            return false;
        }

        return (channelPermission.ModulePermissions & requiredPermission) == requiredPermission;
    }

    /// <summary>
    /// Batched form of <see cref="CanUserPerformActionAsync"/> for fan-out paths that need to know
    /// which of many users may see a channel (Gateway dispatch to installed bots, realtime audience
    /// resolution).
    /// </summary>
    public async Task<List<string>> FilterUsersWithChannelPermissionAsync(
        string channelId,
        IReadOnlyCollection<string> userIds,
        Permissions requiredPermission)
    {
        var allowed = new List<string>(userIds.Count);
        if (userIds.Count == 0) return allowed;

        var channel = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new { c.GuildId, c.Type })
            .FirstOrDefaultAsync();

        if (channel?.GuildId is null)
        {
            logger.LogWarning("Batched permission check failed: channel {ChannelId} not found.", channelId);
            return allowed;
        }

        // Feature gate first, exactly as the single-user path does - a disabled module is off for
        // everybody, owner included.
        if (!GuildFeatureMap.IsPermissionAvailable(await GetGuildFeaturesAsync(channel.GuildId), requiredPermission))
            return allowed;

        // Threads govern posting via SendMessagesInThreads; resolved once rather than per user.
        if (requiredPermission == Permissions.SendMessages && channel.Type == ChannelType.Thread)
            requiredPermission = Permissions.SendMessagesInThreads;

        var ownerId = await ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == channel.GuildId)
            .Select(g => g.OwnerId)
            .FirstOrDefaultAsync();

        foreach (var userId in userIds.Distinct())
        {
            if (string.IsNullOrWhiteSpace(userId)) continue;

            if (userId == ownerId)
            {
                allowed.Add(userId);
                continue;
            }

            var channelPermission = await ResolveChannelPermissionAsync(userId, channel.GuildId, channelId);

            if (channelPermission is not null &&
                (channelPermission.Permissions & requiredPermission) == requiredPermission)
            {
                allowed.Add(userId);
            }
        }

        return allowed;
    }

    /// <inheritdoc cref="FilterUsersWithChannelPermissionAsync(string,IReadOnlyCollection{string},Permissions)"/>
    public async Task<List<string>> FilterUsersWithChannelPermissionAsync(
        string channelId,
        IReadOnlyCollection<string> userIds,
        ModulePermissions requiredPermission)
    {
        var allowed = new List<string>(userIds.Count);
        if (userIds.Count == 0) return allowed;

        var channel = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => new { c.GuildId, c.Type })
            .FirstOrDefaultAsync();

        if (channel?.GuildId is null)
        {
            logger.LogWarning("Batched permission check failed: channel {ChannelId} not found.", channelId);
            return allowed;
        }

        if (!GuildFeatureMap.IsPermissionAvailable(await GetGuildFeaturesAsync(channel.GuildId), requiredPermission))
            return allowed;

        var ownerId = await ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == channel.GuildId)
            .Select(g => g.OwnerId)
            .FirstOrDefaultAsync();

        foreach (var userId in userIds.Distinct())
        {
            if (string.IsNullOrWhiteSpace(userId)) continue;

            if (userId == ownerId)
            {
                allowed.Add(userId);
                continue;
            }

            var channelPermission = await ResolveChannelPermissionAsync(userId, channel.GuildId, channelId);

            if (channelPermission is not null &&
                (channelPermission.ModulePermissions & requiredPermission) == requiredPermission)
            {
                allowed.Add(userId);
            }
        }

        return allowed;
    }

    /// <summary>
    /// Which of <paramref name="channelIds"/> one user holds <paramref name="requiredPermission"/>
    /// on.
    /// </summary>
    public async Task<HashSet<string>> FilterChannelsWithPermissionAsync(
        string userId,
        string guildId,
        IReadOnlyCollection<string> channelIds,
        Permissions requiredPermission)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (channelIds.Count == 0 || string.IsNullOrWhiteSpace(userId)) return allowed;

        // Feature gate first, exactly as both single-channel paths do.
        if (!GuildFeatureMap.IsPermissionAvailable(await GetGuildFeaturesAsync(guildId), requiredPermission))
            return allowed;

        var inGuild = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.GuildId == guildId && channelIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync();

        if (inGuild.Count == 0) return allowed;

        var ownerId = await ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == guildId)
            .Select(g => g.OwnerId)
            .FirstOrDefaultAsync();

        if (userId == ownerId)
        {
            foreach (var channelId in inGuild) allowed.Add(channelId);
            return allowed;
        }

        var resolved = await ComputePermissionsForUserAsync(userId, guildId);

        var byChannel = resolved.Permissions
            .GroupBy(p => p.ChannelId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Permissions, StringComparer.Ordinal);

        foreach (var channelId in inGuild)
        {
            // A channel absent from the cached set is the stale-entry case
            // ResolveChannelPermissionAsync documents.
            if (!byChannel.TryGetValue(channelId, out var permissions))
            {
                var repaired = await ResolveChannelPermissionAsync(userId, guildId, channelId);
                if (repaired is null) continue;
                permissions = repaired.Permissions;
                byChannel[channelId] = permissions;
            }

            if ((permissions & requiredPermission) == requiredPermission) allowed.Add(channelId);
        }

        return allowed;
    }

    /// <summary>
    /// <see
    /// cref="FilterChannelsWithPermissionAsync(string,string,IReadOnlyCollection{string},Permissions)"/>
    /// for a set of channels whose guild the caller does not know, which is every caller reaching
    /// this from another service: a channel id is globally unique here and nothing outside Guild
    /// carries the guild it belongs to.
    /// </summary>
    public async Task<HashSet<string>> FilterChannelsWithPermissionAsync(
        string userId,
        IReadOnlyCollection<string> channelIds,
        Permissions requiredPermission)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (channelIds.Count == 0 || string.IsNullOrWhiteSpace(userId)) return allowed;

        var distinct = channelIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0) return allowed;

        var byGuild = await ctx.Channels
            .AsNoTracking()
            .Where(c => distinct.Contains(c.Id))
            .Select(c => new { c.Id, c.GuildId })
            .ToListAsync();

        foreach (var group in byGuild.GroupBy(c => c.GuildId, StringComparer.Ordinal))
        {
            allowed.UnionWith(await FilterChannelsWithPermissionAsync(
                userId, group.Key, group.Select(c => c.Id).ToList(), requiredPermission));
        }

        return allowed;
    }

    /// <inheritdoc cref="FilterChannelsWithPermissionAsync(string,string,IReadOnlyCollection{string},Permissions)"/>
    public async Task<HashSet<string>> FilterChannelsWithPermissionAsync(
        string userId,
        string guildId,
        IReadOnlyCollection<string> channelIds,
        ModulePermissions requiredPermission)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (channelIds.Count == 0 || string.IsNullOrWhiteSpace(userId)) return allowed;

        if (!GuildFeatureMap.IsPermissionAvailable(await GetGuildFeaturesAsync(guildId), requiredPermission))
            return allowed;

        var inGuild = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.GuildId == guildId && channelIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync();

        if (inGuild.Count == 0) return allowed;

        var ownerId = await ctx.Guilds
            .AsNoTracking()
            .Where(g => g.Id == guildId)
            .Select(g => g.OwnerId)
            .FirstOrDefaultAsync();

        if (userId == ownerId)
        {
            foreach (var channelId in inGuild) allowed.Add(channelId);
            return allowed;
        }

        var resolved = await ComputePermissionsForUserAsync(userId, guildId);

        var byChannel = resolved.Permissions
            .GroupBy(p => p.ChannelId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().ModulePermissions, StringComparer.Ordinal);

        foreach (var channelId in inGuild)
        {
            if (!byChannel.TryGetValue(channelId, out var permissions))
            {
                var repaired = await ResolveChannelPermissionAsync(userId, guildId, channelId);
                if (repaired is null) continue;
                permissions = repaired.ModulePermissions;
                byChannel[channelId] = permissions;
            }

            if ((permissions & requiredPermission) == requiredPermission) allowed.Add(channelId);
        }

        return allowed;
    }

    /// <summary>
    /// The user's resolved permissions for one channel, self-healing against a stale cache.
    /// </summary>
    private async Task<GuildChannelPermission?> ResolveChannelPermissionAsync(
        string userId, string guildId, string channelId)
    {
        var userPermissions = await ComputePermissionsForUserAsync(userId, guildId);
        var channelPermission = userPermissions.Permissions.FirstOrDefault(p => p.ChannelId == channelId);
        if (channelPermission is not null) return channelPermission;

        // A non-member resolves to an empty set by design (see ComputePermissionsForUserAsync), not
        // to a stale one - recomputing would deny again at the cost of a second pass on what is
        // also the hot path for rejecting outsiders.
        if (userPermissions.Permissions.Count == 0 && userPermissions.BasePermissions == Permissions.None)
            return null;

        // Only pay for the recompute when the caller's own entry predates the channel.
        await InvalidateUserPermissionsCacheAsync(guildId, userId);
        userPermissions = await ComputePermissionsForUserAsync(userId, guildId);

        return userPermissions.Permissions.FirstOrDefault(p => p.ChannelId == channelId);
    }

    internal async Task<GuildPermissionsForUser> ComputePermissionsForUserAsync(
        string userId, string guildId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(guildId))
            throw new ArgumentException("UserId and GuildId cannot be null or whitespace");

        var cacheKey = GuildPermissionsForUser.GetCacheKey(guildId, userId);
        var cachedData = await cache.GetStringAsync(cacheKey);

        if (!string.IsNullOrWhiteSpace(cachedData))
        {
            return JsonSerializer.Deserialize<GuildPermissionsForUser>(cachedData)!;
        }

        var (isOwner, userRoleIds, memberId, memberAllow, memberDeny, memberModuleAllow, memberModuleDeny, mutedUntil, onboardingPending) = await GetMembershipAsync(userId, guildId);

        // Fail closed for a non-member.
        if (!isOwner && memberId is null)
        {
            var nonMemberResult = new GuildPermissionsForUser
            {
                GuildId = guildId,
                UserId = userId,
                BasePermissions = Permissions.None,
                BaseModulePermissions = ModulePermissions.None,
                Permissions = [],
            };

            await CachePermissionsAsync(cacheKey, nonMemberResult);
            return nonMemberResult;
        }

        if (isOwner)
        {
            var allChannelIds = await ctx.Channels
                .AsNoTracking()
                .Where(c => c.GuildId == guildId)
                .Select(c => c.Id)
                .ToListAsync();

            var ownerResult = new GuildPermissionsForUser
            {
                GuildId = guildId,
                UserId = userId,
                BasePermissions = ExpandImpliedPermissions(Permissions.Superadmin),
                // The module mask has no Superadmin bit to expand, so the owner's "everything" is
                // spelled out as every bit.
                BaseModulePermissions = AllModulePermissions,
                Permissions = allChannelIds
                    .Select(cid => new GuildChannelPermission
                    {
                        UserId = userId,
                        ChannelId = cid,
                        GuildId = guildId,
                        Permissions = Permissions.Superadmin,
                        ModulePermissions = AllModulePermissions,
                    })
                    .ToList()
            };

            await CachePermissionsAsync(cacheKey, ownerResult);
            return ownerResult;
        }

        // Guild-scoped on purpose.
        var rolePerms = await ctx.Roles
            .AsNoTracking()
            .Where(r => r.GuildId == guildId &&
                        (userRoleIds.Contains(r.Id) || r.Type == RoleType.Everyone))
            .Select(r => new { r.Permissions, r.ModulePermissions })
            .ToListAsync();

        Permissions basePermissions = Permissions.None;
        ModulePermissions baseModulePermissions = ModulePermissions.None;
        foreach (var perm in rolePerms)
        {
            basePermissions |= perm.Permissions;
            baseModulePermissions |= perm.ModulePermissions;
        }

        // Implied bits are resolved here, on the assembled base, and never again.
        basePermissions = ExpandImpliedPermissions(basePermissions);

        // Member-level guild overrides take precedence over roles but are applied before
        // channel/category overwrites, and follow the same allow/deny rules an overwrite does: the
        // deny carries its transitive closure (denying ViewChannel here denies everything that
        // implies it), the allow grants exactly the bits named and nothing more.
        basePermissions &= ~ExpandDeniedPermissions(memberDeny);
        basePermissions |= memberAllow;

        baseModulePermissions &= ~memberModuleDeny;
        baseModulePermissions |= memberModuleAllow;

        var channels = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.GuildId == guildId)
            .Select(c => new
            {
                c.Id,
                c.GuildId,
                c.CategoryId,
                c.Type,
                c.ParentChannelId,
            })
            .ToListAsync();

        var channelIds = channels.Select(c => c.Id).ToList();

        var categoryIds = channels
            .Where(c => !string.IsNullOrWhiteSpace(c.CategoryId))
            .Select(c => c.CategoryId!)
            .Distinct()
            .ToList();

        var allOverwrites = await ctx.Set<ChannelPermission>()
            .AsNoTracking()
            .Include(p => p.Role) 
            .Where(p =>
                (p.ChannelId != null && channelIds.Contains(p.ChannelId)) ||
                (p.CategoryId != null && categoryIds.Contains(p.CategoryId)))
            .ToListAsync();

        var overwritesByChannel = allOverwrites
            .Where(p => p.ChannelId != null)
            .GroupBy(p => p.ChannelId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var overwritesByCategory = allOverwrites
            .Where(p => p.CategoryId != null && p.ChannelId == null)
            .GroupBy(p => p.CategoryId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var channelPermissions = new List<GuildChannelPermission>(channels.Count);

        // Threads have no independent overwrites in this pass - resolve them in a second
        // pass so their parent (processed in the loop below) is already computed.
        var nonThreadChannels = channels.Where(c => c.Type != ChannelType.Thread || c.ParentChannelId == null).ToList();
        var threadChannels = channels.Where(c => c.Type == ChannelType.Thread && c.ParentChannelId != null).ToList();

        foreach (var channel in nonThreadChannels)
        {
            Permissions resolvedPermissions = basePermissions;
            ModulePermissions resolvedModulePermissions = baseModulePermissions;

            if (!string.IsNullOrWhiteSpace(channel.CategoryId) &&
                overwritesByCategory.TryGetValue(channel.CategoryId!, out var categoryOverwrites))
            {
                var tiers = BucketOverwrites(categoryOverwrites, memberId, userRoleIds);

                resolvedPermissions = ApplyOverwrites(resolvedPermissions, tiers);
                resolvedModulePermissions = ApplyModuleOverwrites(resolvedModulePermissions, tiers);
            }

            if (overwritesByChannel.TryGetValue(channel.Id, out var channelOverwrites))
            {
                var tiers = BucketOverwrites(channelOverwrites, memberId, userRoleIds);

                resolvedPermissions = ApplyOverwrites(resolvedPermissions, tiers);
                resolvedModulePermissions = ApplyModuleOverwrites(resolvedModulePermissions, tiers);
            }

            channelPermissions.Add(new GuildChannelPermission
            {
                UserId = userId,
                ChannelId = channel.Id,
                GuildId = guildId,
                Permissions = resolvedPermissions,
                ModulePermissions = ExpandModuleForSuperadmin(resolvedPermissions, resolvedModulePermissions),
            });
        }

        foreach (var thread in threadChannels)
        {
            var parentResult = channelPermissions.FirstOrDefault(p => p.ChannelId == thread.ParentChannelId);

            channelPermissions.Add(new GuildChannelPermission
            {
                UserId = userId,
                ChannelId = thread.Id,
                GuildId = guildId,
                Permissions = parentResult?.Permissions ?? basePermissions,
                ModulePermissions = parentResult?.ModulePermissions ?? baseModulePermissions,
            });
        }

        var expandedBase = basePermissions;

        // A timed-out member, and a member who has not yet accepted onboarding, are cut back to
        // reading: they keep exactly MuteRetainedPermissions and lose everything else.
        if (((mutedUntil is not null && mutedUntil > DateTimeOffset.UtcNow) || onboardingPending)
            && !expandedBase.HasFlag(Permissions.Superadmin))
        {
            expandedBase &= MuteRetainedPermissions;
            foreach (var channelPermission in channelPermissions)
                channelPermission.Permissions &= MuteRetainedPermissions;
        }

        var result = new GuildPermissionsForUser
        {
            GuildId = guildId,
            UserId = userId,
            BasePermissions = expandedBase,
            // No implication pass beyond Superadmin: no module permission implies another one.
            BaseModulePermissions = ExpandModuleForSuperadmin(basePermissions, baseModulePermissions),
            Permissions = channelPermissions
        };

        await CachePermissionsAsync(cacheKey, result);
        return result;
    }

    /// <summary>
    /// Everything a timed-out member - or one who has not accepted onboarding - still holds.
    /// </summary>
    private const Permissions MuteRetainedPermissions =
        Permissions.ViewChannel | Permissions.ReadMessageHistory;

    /// <summary>
    /// The three tiers of overwrite that apply to one member on one channel or category, each
    /// already unioned across every row in that tier.
    /// </summary>
    private readonly record struct OverwriteTiers(
        Permissions EveryoneDeny, Permissions EveryoneAllow,
        Permissions RoleDeny, Permissions RoleAllow,
        Permissions MemberDeny, Permissions MemberAllow,
        ModulePermissions EveryoneModuleDeny, ModulePermissions EveryoneModuleAllow,
        ModulePermissions RoleModuleDeny, ModulePermissions RoleModuleAllow,
        ModulePermissions MemberModuleDeny, ModulePermissions MemberModuleAllow);

    /// <summary>
    /// Buckets the overwrites that apply to this member into the three tiers Discord's resolution
    /// algorithm names, unioning both masks of both enums as it goes.
    /// </summary>
    private static OverwriteTiers BucketOverwrites(
        IReadOnlyList<ChannelPermission> overwrites,
        string? memberId,
        IReadOnlyList<string> roleIds)
    {
        var tiers = new OverwriteTiers();

        foreach (var overwrite in overwrites)
        {
            if (memberId != null && overwrite.MemberId == memberId)
            {
                tiers = tiers with
                {
                    MemberDeny = tiers.MemberDeny | overwrite.DenyPermissions,
                    MemberAllow = tiers.MemberAllow | overwrite.AllowPermissions,
                    MemberModuleDeny = tiers.MemberModuleDeny | overwrite.DenyModulePermissions,
                    MemberModuleAllow = tiers.MemberModuleAllow | overwrite.AllowModulePermissions,
                };
                continue;
            }

            if (overwrite.RoleId is null) continue;

            if (overwrite.Role?.Type == RoleType.Everyone)
            {
                tiers = tiers with
                {
                    EveryoneDeny = tiers.EveryoneDeny | overwrite.DenyPermissions,
                    EveryoneAllow = tiers.EveryoneAllow | overwrite.AllowPermissions,
                    EveryoneModuleDeny = tiers.EveryoneModuleDeny | overwrite.DenyModulePermissions,
                    EveryoneModuleAllow = tiers.EveryoneModuleAllow | overwrite.AllowModulePermissions,
                };
                continue;
            }

            if (!roleIds.Contains(overwrite.RoleId)) continue;

            tiers = tiers with
            {
                RoleDeny = tiers.RoleDeny | overwrite.DenyPermissions,
                RoleAllow = tiers.RoleAllow | overwrite.AllowPermissions,
                RoleModuleDeny = tiers.RoleModuleDeny | overwrite.DenyModulePermissions,
                RoleModuleAllow = tiers.RoleModuleAllow | overwrite.AllowModulePermissions,
            };
        }

        return tiers;
    }

    /// <summary>
    /// Resolves one layer of overwrites - a category's or a channel's - onto an already expanded
    /// mask, in Discord's documented order: the @everyone overwrite (deny then allow), then every
    /// held role's overwrites unioned (deny then allow, so an allow on one role beats a deny on
    /// another), then the member's own overwrite last.
    /// </summary>
    private static Permissions ApplyOverwrites(Permissions initial, in OverwriteTiers tiers)
    {
        if (initial.HasFlag(Permissions.Superadmin)) return initial;

        var result = initial;

        result &= ~ExpandDeniedPermissions(tiers.EveryoneDeny);
        result |= tiers.EveryoneAllow;

        result &= ~ExpandDeniedPermissions(tiers.RoleDeny);
        result |= tiers.RoleAllow;

        result &= ~ExpandDeniedPermissions(tiers.MemberDeny);
        result |= tiers.MemberAllow;

        return result;
    }

    /// <summary>
    /// The module-mask twin of <see cref="ApplyOverwrites"/>, deliberately kept as a separate
    /// method with the identical shape rather than folded into it.
    /// </summary>
    private static ModulePermissions ApplyModuleOverwrites(ModulePermissions initial, in OverwriteTiers tiers)
    {
        var result = initial;

        result &= ~tiers.EveryoneModuleDeny;
        result |= tiers.EveryoneModuleAllow;

        result &= ~tiers.RoleModuleDeny;
        result |= tiers.RoleModuleAllow;

        result &= ~tiers.MemberModuleDeny;
        result |= tiers.MemberModuleAllow;

        return result;
    }

    /// <summary>Every bit of the module mask, used where the core mask would use Superadmin.</summary>
    internal const ModulePermissions AllModulePermissions = (ModulePermissions)ulong.MaxValue;

    /// <summary>Carries the core mask's Superadmin bit across to the module mask.</summary>
    private static ModulePermissions ExpandModuleForSuperadmin(Permissions core, ModulePermissions module) =>
        core.HasFlag(Permissions.Superadmin) ? AllModulePermissions : module;

    /// <summary>
    /// Every "holding X means you also hold Y" rule in the core permission mask, as data.
    /// </summary>
    private static readonly (Permissions Holder, Permissions Implied)[] ImpliedPermissions =
    [
        (Permissions.EditAnyMessage,        Permissions.EditOwnMessages),
        (Permissions.DeleteAnyMessage,      Permissions.DeleteOwnMessages),
        (Permissions.ManageAnyThread,       Permissions.ManageOwnThreads),

        (Permissions.Speak,                 Permissions.Connect),
        (Permissions.Stream,                Permissions.Connect),
        (Permissions.MuteMembers,           Permissions.Connect),
        (Permissions.DeafenMembers,         Permissions.Connect),
        (Permissions.MoveMembers,           Permissions.Connect),

        (Permissions.PinMessages,           Permissions.SendMessages),
        (Permissions.AttachFiles,           Permissions.SendMessages),
        (Permissions.EmbedLinks,            Permissions.SendMessages),
        (Permissions.AddReactions,          Permissions.SendMessages),
        (Permissions.CreateThreads,         Permissions.SendMessages),

        (Permissions.SendMessages,          Permissions.ViewChannel),
        (Permissions.SendMessagesInThreads, Permissions.ViewChannel),
        (Permissions.Connect,               Permissions.ViewChannel),
        (Permissions.EditOwnMessages,       Permissions.ViewChannel),
        (Permissions.DeleteOwnMessages,     Permissions.ViewChannel),
        (Permissions.ManageOwnThreads,      Permissions.ViewChannel),
        (Permissions.ManagePermissions,     Permissions.ViewChannel),
        (Permissions.ManageChannel,         Permissions.ViewChannel),
    ];

    private const int PermissionBitCount = 64;

    /// <summary>For each bit position, every bit a holder of it also holds, transitively.</summary>
    private static readonly Permissions[] ForwardClosure = BuildClosure(reverse: false);

    /// <summary>For each bit position, every bit whose holder would thereby hold it, transitively -
    /// the set a deny of that bit has to take with it.</summary>
    private static readonly Permissions[] ReverseClosure = BuildClosure(reverse: true);

    /// <summary>Computes the transitive closure of <see cref="ImpliedPermissions"/> once per
    /// direction at type-initialization time, so resolving a mask is a handful of ORs rather than a
    /// graph walk on a path that runs on every message send.</summary>
    private static Permissions[] BuildClosure(bool reverse)
    {
        var direct = new Permissions[PermissionBitCount];
        foreach (var (holder, implied) in ImpliedPermissions)
        {
            var from = reverse ? implied : holder;
            var to = reverse ? holder : implied;
            direct[BitOperations.TrailingZeroCount((ulong)from)] |= to;
        }

        var closure = new Permissions[PermissionBitCount];
        for (var bit = 0; bit < PermissionBitCount; bit++)
        {
            var reached = (Permissions)(1ul << bit);
            var frontier = direct[bit];

            while ((frontier & ~reached) != Permissions.None)
            {
                var fresh = frontier & ~reached;
                reached |= fresh;

                frontier = Permissions.None;
                var remaining = (ulong)fresh;
                while (remaining != 0)
                {
                    frontier |= direct[BitOperations.TrailingZeroCount(remaining)];
                    remaining &= remaining - 1;
                }
            }

            closure[bit] = reached;
        }

        return closure;
    }

    private static Permissions Close(Permissions mask, Permissions[] closure)
    {
        var result = mask;
        var remaining = (ulong)mask;
        while (remaining != 0)
        {
            result |= closure[BitOperations.TrailingZeroCount(remaining)];
            remaining &= remaining - 1;
        }

        return result;
    }

    /// <summary>Widens a granted mask with everything its bits imply.</summary>
    internal static Permissions ExpandImpliedPermissions(Permissions p)
    {
        // Superadmin short-circuits everything - no need to enumerate.
        if (p.HasFlag(Permissions.Superadmin))
            return p | ~Permissions.None;

        return Close(p, ForwardClosure);
    }

    /// <summary>
    /// Widens a denied mask with everything that implies its bits, so that subtracting the result
    /// cannot leave behind a bit whose meaning includes the one being taken away.
    /// </summary>
    internal static Permissions ExpandDeniedPermissions(Permissions p) => Close(p, ReverseClosure);

    private async Task CachePermissionsAsync(string cacheKey, GuildPermissionsForUser permissions)
    {
        var serialized = JsonSerializer.Serialize(permissions);
        await cache.SetStringAsync(cacheKey, serialized, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15)
        });
    }

    /// <summary>The caller's fully-resolved guild-scoped permissions, as a single mask.</summary>
    public async Task<Permissions> GetGuildPermissionsAsync(string userId, string guildId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(guildId))
            throw new ArgumentException("UserId and GuildId cannot be null or whitespace");

        var resolved = await ComputePermissionsForUserAsync(userId, guildId);

        // The same gate CanUserPerformActionOnGuildAsync applies per check, applied once to the
        // whole mask.
        var features = await GetGuildFeaturesAsync(guildId);
        return GuildFeatureMap.ClampToEnabled(features, resolved.BasePermissions);
    }

    /// <summary>The module-mask half of <see cref="GetGuildPermissionsAsync"/>.</summary>
    public async Task<ModulePermissions> GetGuildModulePermissionsAsync(string userId, string guildId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(guildId))
            throw new ArgumentException("UserId and GuildId cannot be null or whitespace");

        var resolved = await ComputePermissionsForUserAsync(userId, guildId);

        var features = await GetGuildFeaturesAsync(guildId);
        return GuildFeatureMap.ClampToEnabled(features, resolved.BaseModulePermissions);
    }

    public async Task<bool> CanUserPerformActionOnGuildAsync(
        string userId,
        string guildId,
        Permissions requiredPermission)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(guildId))
            throw new ArgumentException("UserId and GuildId cannot be null or whitespace");

        // See CanUserPerformActionAsync - the feature gate runs before permission resolution so
        // no role, override or ownership can escalate past a disabled module.
        if (!GuildFeatureMap.IsPermissionAvailable(await GetGuildFeaturesAsync(guildId), requiredPermission))
            return false;

        var userPermissions = await ComputePermissionsForUserAsync(userId, guildId);
        return (userPermissions.BasePermissions & requiredPermission) == requiredPermission;
    }

    public async Task<bool> CanUserPerformActionOnGuildAsync(
        ClaimsPrincipal user,
        string guildId,
        Permissions requiredPermission)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return false;
        return await CanUserPerformActionOnGuildAsync(userId, guildId, requiredPermission);
    }

    /// <inheritdoc cref="CanUserPerformActionOnGuildAsync(string,string,Permissions)"/>
    public async Task<bool> CanUserPerformActionOnGuildAsync(
        string userId,
        string guildId,
        ModulePermissions requiredPermission)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(guildId))
            throw new ArgumentException("UserId and GuildId cannot be null or whitespace");

        if (!GuildFeatureMap.IsPermissionAvailable(await GetGuildFeaturesAsync(guildId), requiredPermission))
            return false;

        var userPermissions = await ComputePermissionsForUserAsync(userId, guildId);
        return (userPermissions.BaseModulePermissions & requiredPermission) == requiredPermission;
    }

    /// <inheritdoc cref="CanUserPerformActionOnGuildAsync(string,string,ModulePermissions)"/>
    public async Task<bool> CanUserPerformActionOnGuildAsync(
        ClaimsPrincipal user,
        string guildId,
        ModulePermissions requiredPermission)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return false;
        return await CanUserPerformActionOnGuildAsync(userId, guildId, requiredPermission);
    }

    public async Task InvalidateUserPermissionsCacheAsync(string guildId, string userId)
    {
        var cacheKey = GuildPermissionsForUser.GetCacheKey(guildId, userId);
        await cache.RemoveAsync(cacheKey);
    }

    /// <summary>
    /// Guards against privilege escalation: an actor may only grant permission bits they themselves
    /// currently hold (guild owner is exempt, matching the Superadmin short-circuit already used
    /// throughout this service).
    /// </summary>
    public async Task<bool> CanGrantPermissionsAsync(string actorUserId, string guildId, Permissions requestedPermissions)
    {
        var clamped = await ClampToGrantableAsync(actorUserId, guildId, requestedPermissions);
        return clamped == requestedPermissions;
    }

    /// <summary>
    /// Same escalation guard as <see cref="CanGrantPermissionsAsync"/>, but returns the
    /// clamped bitmask (requested &amp; actor's own base permissions) instead of a bool -
    /// used where the caller wants to silently downgrade an over-broad request (e.g. a bot
    /// install requesting more permissions than the installer holds) rather than reject it.
    /// </summary>
    public async Task<Permissions> ClampToGrantableAsync(string actorUserId, string guildId, Permissions requested)
    {
        // Clamped against the guild's modules as well as the actor's own bits - an owner holds
        // every permission, so without this a bot installed by the owner would come away holding
        // permissions for modules the guild doesn't have.
        var enabled = await GetGuildFeaturesAsync(guildId);
        requested = GuildFeatureMap.ClampToEnabled(enabled, requested);

        var actorPermissions = await ComputePermissionsForUserAsync(actorUserId, guildId);
        return requested & actorPermissions.BasePermissions;
    }

    /// <inheritdoc cref="CanGrantPermissionsAsync(string,string,Permissions)"/>
    public async Task<bool> CanGrantPermissionsAsync(string actorUserId, string guildId, ModulePermissions requestedPermissions)
    {
        var clamped = await ClampToGrantableAsync(actorUserId, guildId, requestedPermissions);
        return clamped == requestedPermissions;
    }

    /// <inheritdoc cref="ClampToGrantableAsync(string,string,Permissions)"/>
    public async Task<ModulePermissions> ClampToGrantableAsync(string actorUserId, string guildId, ModulePermissions requested)
    {
        var enabled = await GetGuildFeaturesAsync(guildId);
        requested = GuildFeatureMap.ClampToEnabled(enabled, requested);

        var actorPermissions = await ComputePermissionsForUserAsync(actorUserId, guildId);
        return requested & actorPermissions.BaseModulePermissions;
    }

    /// <summary>Highest role Position the user holds in this guild.</summary>
    public async Task<int> GetHighestRolePositionAsync(string userId, string guildId)
    {
        var (isOwner, roleIds, memberId, _, _, _, _, _, _) = await GetMembershipAsync(userId, guildId);
        if (isOwner) return int.MaxValue;

        if (memberId is null) return int.MinValue;

        // Guild-scoped for the same reason as ComputePermissionsForUserAsync: a foreign role's
        // Position must not be able to inflate the actor's rank and defeat CanManageRoleAsync /
        // CanModerateTargetAsync. MaxAsync throws on an empty sequence, so this filters first.
        var positions = await ctx.Roles
            .AsNoTracking()
            .Where(r => r.GuildId == guildId &&
                        (roleIds.Contains(r.Id) || r.Type == RoleType.Everyone))
            .Select(r => r.Position)
            .ToListAsync();

        return positions.Count == 0 ? int.MinValue : positions.Max();
    }

    /// <summary>
    /// True if the actor's highest role outranks the target role (or the actor is guild owner).
    /// </summary>
    public async Task<bool> CanManageRoleAsync(string actorUserId, string guildId, string targetRoleId)
    {
        var actorPosition = await GetHighestRolePositionAsync(actorUserId, guildId);
        if (actorPosition == int.MaxValue) return true;

        var targetPosition = await ctx.Roles
            .AsNoTracking()
            .Where(r => r.Id == targetRoleId && r.GuildId == guildId)
            .Select(r => (int?)r.Position)
            .FirstOrDefaultAsync();

        if (targetPosition is null) return false;

        return actorPosition > targetPosition.Value;
    }

    /// <summary>True if the actor outranks the target member (or is guild owner).</summary>
    public async Task<bool> CanModerateTargetAsync(string actorUserId, string targetUserId, string guildId)
    {
        var isTargetOwner = await ctx.Guilds
            .AsNoTracking()
            .AnyAsync(g => g.Id == guildId && g.OwnerId == targetUserId);
        if (isTargetOwner) return false;

        var actorPosition = await GetHighestRolePositionAsync(actorUserId, guildId);
        if (actorPosition == int.MaxValue) return true;

        var targetPosition = await GetHighestRolePositionAsync(targetUserId, guildId);
        return actorPosition > targetPosition;
    }
}
