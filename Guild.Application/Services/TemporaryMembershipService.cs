using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// The scheduling half of temporary membership: what a socket closing and a socket opening do to a
/// member who joined on a <see cref="Domain.Entity.GuildInvite.Temporary"/> invite.
/// </summary>
public static class TemporaryMembershipService
{
    /// <summary>
    /// How long a temporary member may be offline before the sweep takes their membership.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Marks this account's temporary memberships as due, in every guild where it has not earned a
    /// role beyond @everyone.
    /// </summary>
    public static async Task ScheduleEvictionsAsync(string userId, MicroserviceContext ctx, DateTimeOffset now,
        CancellationToken ct = default)
    {
        var candidates = await ctx.GuildMembers
            .Where(m => m.UserId == userId && m.TemporaryMembership)
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        var memberIds = candidates.Select(m => m.Id).ToList();

        var earnedARole = await ctx.RoleMembers
            .Where(rm => memberIds.Contains(rm.MemberId) && rm.Role.Type != RoleType.Everyone)
            .Select(rm => rm.MemberId)
            .Distinct()
            .ToListAsync(ct);

        var permanent = earnedARole.ToHashSet(StringComparer.Ordinal);

        foreach (var member in candidates)
        {
            member.TemporaryEvictionDueAt = permanent.Contains(member.Id) ? null : now + Grace;
        }
    }

    /// <summary>Cancels every pending eviction for this account, because it is back.</summary>
    public static async Task CancelEvictionsAsync(string userId, MicroserviceContext ctx, CancellationToken ct = default)
    {
        var pending = await ctx.GuildMembers
            .Where(m => m.UserId == userId && m.TemporaryEvictionDueAt != null)
            .ToListAsync(ct);

        foreach (var member in pending) member.TemporaryEvictionDueAt = null;
    }
}
