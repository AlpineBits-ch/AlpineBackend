using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

/// <summary>
/// Answers <see cref="GetSharedGuildsRequest"/> - the contract behind Social's
/// <c>FriendRequestPolicy.ServerMembers</c> branch and its <c>mutualServers</c> field.
/// </summary>
public class GetSharedGuildsHandler
{
    public static async Task<GetSharedGuildsResponse> Handle(
        GetSharedGuildsRequest request, MicroserviceContext ctx, BlockCache blocks)
    {
        if (string.IsNullOrWhiteSpace(request.UserId)) return new GetSharedGuildsResponse();

        var others = request.OtherUserIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            // Self-pairs are dropped, not answered: intersecting a user with themselves is their
            // entire guild list, which is precisely the enumeration this contract must not offer.
            .Where(id => !string.Equals(id, request.UserId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (others.Count == 0) return new GetSharedGuildsResponse();

        var subjectGuildIds = await BuildMembershipQuery(ctx, request.UserId).ToListAsync();
        if (subjectGuildIds.Count == 0) return new GetSharedGuildsResponse();

        var pairs = await BuildSharedMembershipQuery(ctx, others, subjectGuildIds).ToListAsync();
        if (pairs.Count == 0) return new GetSharedGuildsResponse();

        var blockView = await blocks.GetAsync([request.UserId]);

        return new GetSharedGuildsResponse
        {
            Shared = pairs
                .Where(p => !blockView.AreBlocked(request.UserId, p.UserId))
                .GroupBy(p => p.UserId, StringComparer.Ordinal)
                .Select(group => new SharedGuildsSummary
                {
                    OtherUserId = group.Key,
                    GuildIds = group
                        .Select(p => p.GuildId)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToList(),
                })
                .ToList(),
        };
    }

    /// <summary>One user's guild ids.</summary>
    public static IQueryable<string> BuildMembershipQuery(MicroserviceContext ctx, string userId) =>
        ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.GuildId)
            .Distinct();

    /// <summary>
    /// The (user, guild) rows of <paramref name="otherUserIds"/> restricted to <paramref
    /// name="subjectGuildIds"/> - the intersection itself, in one query for the batch.
    /// </summary>
    public static IQueryable<SharedMembershipRow> BuildSharedMembershipQuery(
        MicroserviceContext ctx, IReadOnlyCollection<string> otherUserIds, IReadOnlyCollection<string> subjectGuildIds)
    {
        var others = otherUserIds.ToList();
        var guilds = subjectGuildIds.ToList();

        return ctx.GuildMembers
            .AsNoTracking()
            .Where(m => others.Contains(m.UserId) && guilds.Contains(m.GuildId))
            .Select(m => new SharedMembershipRow(m.UserId, m.GuildId))
            .Distinct();
    }

    /// <summary>Named rather than anonymous so the query builder can be a separate method the
    /// translation test can call.</summary>
    public readonly record struct SharedMembershipRow(string UserId, string GuildId);
}
