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

public class GuildChannelPermission
{
    public string UserId { get; set; }
    public string ChannelId { get; set; }
    public string GuildId { get; set; }
    public Permissions Permissions { get; set; }

    public static string GetCacheKey(string guildId, string channelId, string userId)
    {
        var g = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(guildId));
        var c = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(channelId));
        var u = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(userId));
        return $"guild:{g}:channel:{c}:user:{u}";
    }

    public string GetCacheKey() => GetCacheKey(GuildId, ChannelId, UserId);
}

public class GuildPermissionsForUser
{
    public string UserId { get; set; }
    public string GuildId { get; set; }
    public Permissions BasePermissions { get; set; }
    public ICollection<GuildChannelPermission> Permissions { get; set; }

    public static string GetCacheKey(string guildId, string userId)
    {
        var g = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(guildId));
        var u = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(userId));
        return $"guild:{g}:user:{u}";
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

    private async Task<(bool isOwner, List<string> roleIds, string? memberId, Permissions memberAllow, Permissions memberDeny, DateTimeOffset? mutedUntil, bool onboardingPending)> GetMembershipAsync(
        string userId, string guildId)
    {
        var isOwner = await ctx.Guilds
            .AsNoTracking()
            .AnyAsync(g => g.Id == guildId && g.OwnerId == userId);

        var memberRow = await ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.GuildId == guildId)
            .Select(m => new { m.Id, m.AllowPermissions, m.DenyPermissions, m.MutedUntil, m.OnboardingCompletedAt })
            .FirstOrDefaultAsync();

        var memberId = memberRow?.Id;
        var memberAllow = memberRow?.AllowPermissions ?? Permissions.None;
        var memberDeny = memberRow?.DenyPermissions ?? Permissions.None;
        var mutedUntil = memberRow?.MutedUntil;
        var onboardingPending = memberRow is not null && memberRow.OnboardingCompletedAt is null;

        var roleIds = memberId == null
            ? []
            : await ctx.RoleMembers
                .AsNoTracking()
                .Where(rm => rm.MemberId == memberId)
                .Select(rm => rm.RoleId)
                .ToListAsync();

        return (isOwner, roleIds, memberId, memberAllow, memberDeny, mutedUntil, onboardingPending);
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

        var (isOwner, _, _, _, _, _, _) = await GetMembershipAsync(userId, guildId);
        if (isOwner) return true;

        var userPermissions = await ComputePermissionsForUserAsync(userId, guildId);
        var channelPermission = userPermissions.Permissions
            .FirstOrDefault(p => p.ChannelId == channelId);

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

        var (isOwner, userRoleIds, memberId, memberAllow, memberDeny, mutedUntil, onboardingPending) = await GetMembershipAsync(userId, guildId);

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
                Permissions = allChannelIds
                    .Select(cid => new GuildChannelPermission
                    {
                        UserId = userId,
                        ChannelId = cid,
                        GuildId = guildId,
                        Permissions = Permissions.Superadmin
                    })
                    .ToList()
            };

            await CachePermissionsAsync(cacheKey, ownerResult);
            return ownerResult;
        }

        var rolePerms = await ctx.Roles
            .AsNoTracking()
            .Where(r => userRoleIds.Contains(r.Id))
            .Select(r => r.Permissions)
            .ToListAsync();

        Permissions basePermissions = Permissions.None;
        foreach (var perm in rolePerms)
        {
            basePermissions |= perm;
        }

        // Member-level guild overrides take precedence over roles but are
        // applied before channel/category overwrites.
        basePermissions &= ~memberDeny;
        basePermissions |= memberAllow;

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

        // Threads have no independent overwrites in this pass — resolve them in a second
        // pass so their parent (processed in the loop below) is already computed.
        var nonThreadChannels = channels.Where(c => c.Type != ChannelType.Thread || c.ParentChannelId == null).ToList();
        var threadChannels = channels.Where(c => c.Type == ChannelType.Thread && c.ParentChannelId != null).ToList();

        foreach (var channel in nonThreadChannels)
        {
            Permissions resolvedPermissions = basePermissions;

            if (!string.IsNullOrWhiteSpace(channel.CategoryId) &&
                overwritesByCategory.TryGetValue(channel.CategoryId!, out var categoryOverwrites))
            {
                var relevant = categoryOverwrites
                    .Where(p => userRoleIds.Contains(p.RoleId) ||
                                (memberId != null && p.MemberId == memberId) ||
                                p.Role?.Type == RoleType.Everyone)
                    .ToList();

                resolvedPermissions = ApplyOverwrites(resolvedPermissions, relevant, memberId, userRoleIds);
            }

            if (overwritesByChannel.TryGetValue(channel.Id, out var channelOverwrites))
            {
                var relevant = channelOverwrites
                    .Where(p => userRoleIds.Contains(p.RoleId) ||
                                (memberId != null && p.MemberId == memberId) ||
                                p.Role?.Type == RoleType.Everyone)
                    .ToList();

                resolvedPermissions = ApplyOverwrites(resolvedPermissions, relevant, memberId, userRoleIds);
            }

            channelPermissions.Add(new GuildChannelPermission
            {
                UserId = userId,
                ChannelId = channel.Id,
                GuildId = guildId,
                Permissions = ExpandImpliedPermissions(resolvedPermissions)
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
                Permissions = parentResult?.Permissions ?? ExpandImpliedPermissions(basePermissions)
            });
        }

        var expandedBase = ExpandImpliedPermissions(basePermissions);

        // A muted (timed-out) member keeps their other permissions but loses the ability to speak:
        // sending messages, reacting, starting threads, or connecting to voice.
        if (((mutedUntil is not null && mutedUntil > DateTimeOffset.UtcNow) || onboardingPending)
            && !expandedBase.HasFlag(Permissions.Superadmin))
        {
            expandedBase &= ~MuteStrippedPermissions;
            foreach (var channelPermission in channelPermissions)
                channelPermission.Permissions &= ~MuteStrippedPermissions;
        }

        var result = new GuildPermissionsForUser
        {
            GuildId = guildId,
            UserId = userId,
            BasePermissions = expandedBase,
            Permissions = channelPermissions
        };

        await CachePermissionsAsync(cacheKey, result);
        return result;
    }

    private const Permissions MuteStrippedPermissions =
        Permissions.SendMessages | Permissions.SendMessagesInThreads | Permissions.AddReactions |
        Permissions.CreateThreads | Permissions.Connect;

    private Permissions ApplyOverwrites(
        Permissions initial,
        IReadOnlyList<ChannelPermission> overwrites,
        string? memberId,
        IReadOnlyList<string> roleIds)
    {
        var result = initial;

        var everyoneOverwrite = overwrites.FirstOrDefault(o => o.Role?.Type == RoleType.Everyone);
        if (everyoneOverwrite != null)
        {
            result &= ~everyoneOverwrite.DenyPermissions;
            result |= everyoneOverwrite.AllowPermissions;
        }

        var roleOverwrites = overwrites
            .Where(o => o.RoleId != null &&
                        o.Role?.Type != RoleType.Everyone &&
                        roleIds.Contains(o.RoleId))
            .ToList();

        foreach (var overwrite in roleOverwrites)
        {
            result &= ~overwrite.DenyPermissions;
            result |= overwrite.AllowPermissions;
        }

        var memberOverwrite = memberId != null
            ? overwrites.FirstOrDefault(o => o.MemberId == memberId)
            : null;

        if (memberOverwrite != null)
        {
            result &= ~memberOverwrite.DenyPermissions;
            result |= memberOverwrite.AllowPermissions;
        }

        return result;
    }

    private static Permissions ExpandImpliedPermissions(Permissions p)
    {
        // Superadmin short-circuits everything — no need to enumerate.
        if (p.HasFlag(Permissions.Superadmin))
            return p | ~Permissions.None;

        // Each block grants the permissions that the held flag logically implies.

        if (p.HasFlag(Permissions.EditAnyMessage))
            p |= Permissions.EditOwnMessages;

        if (p.HasFlag(Permissions.DeleteAnyMessage))
            p |= Permissions.DeleteOwnMessages;

        if (p.HasFlag(Permissions.ManageAnyThread))
            p |= Permissions.ManageOwnThreads;

        if (p.HasFlag(Permissions.Stream))
            p |= Permissions.Speak;

        if (p.HasFlag(Permissions.Speak))
            p |= Permissions.Stream;

        // Speak/Connect implication handled below via Speak flag
        if (p.HasFlag(Permissions.Speak) ||
            p.HasFlag(Permissions.MuteMembers) ||
            p.HasFlag(Permissions.DeafenMembers) ||
            p.HasFlag(Permissions.MoveMembers))
            p |= Permissions.Connect;

        if (p.HasFlag(Permissions.PinMessages) ||
            p.HasFlag(Permissions.AttachFiles) ||
            p.HasFlag(Permissions.EmbedLinks) ||
            p.HasFlag(Permissions.AddReactions) ||
            p.HasFlag(Permissions.CreateThreads))
            p |= Permissions.SendMessages;

        if (p.HasFlag(Permissions.ManagePermissions))
            p |= Permissions.ViewChannel;

        if (p.HasFlag(Permissions.ManageChannel))
            p |= Permissions.ViewChannel | Permissions.ManagePermissions;

        // Anything that implies SendMessages or Connect also implies ViewChannel.
        if (p.HasFlag(Permissions.SendMessages) ||
            p.HasFlag(Permissions.SendMessagesInThreads) ||
            p.HasFlag(Permissions.Connect) ||
            p.HasFlag(Permissions.EditOwnMessages) ||
            p.HasFlag(Permissions.DeleteOwnMessages) ||
            p.HasFlag(Permissions.ManageOwnThreads) ||
            p.HasFlag(Permissions.ManageAnyThread))
            p |= Permissions.ViewChannel;

        return p;
    }

    private async Task CachePermissionsAsync(string cacheKey, GuildPermissionsForUser permissions)
    {
        var serialized = JsonSerializer.Serialize(permissions);
        await cache.SetStringAsync(cacheKey, serialized, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15)
        });
    }

    public async Task<bool> CanUserPerformActionOnGuildAsync(
        string userId,
        string guildId,
        Permissions requiredPermission)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(guildId))
            throw new ArgumentException("UserId and GuildId cannot be null or whitespace");

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
        var actorPermissions = await ComputePermissionsForUserAsync(actorUserId, guildId);
        return requested & actorPermissions.BasePermissions;
    }

    /// <summary>Highest role Position the user holds in this guild.</summary>
    public async Task<int> GetHighestRolePositionAsync(string userId, string guildId)
    {
        var (isOwner, roleIds, _, _, _, _, _) = await GetMembershipAsync(userId, guildId);
        if (isOwner) return int.MaxValue;

        if (roleIds.Count == 0) return int.MinValue;

        return await ctx.Roles
            .AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .MaxAsync(r => r.Position);
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
