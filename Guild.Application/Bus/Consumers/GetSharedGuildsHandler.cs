using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

/// <summary>
/// Answers <see cref="GetSharedGuildsRequest"/> - the contract behind Social's
/// <c>FriendRequestPolicy.ServerMembers</c> branch and its <c>mutualServers</c> field.
///
/// <para><b>Only pairs, never a roster.</b> The intersection is computed against the subject's own
/// membership, so every guild returned is one the subject is already in. That is what keeps this
/// from being a co-membership oracle: it cannot tell a caller anything about a guild the subject is
/// not in, and it has no mode that lists a stranger's guilds. Guilds have no visibility concept
/// beyond membership in this model - a guild is visible exactly to its members - so there is no
/// further "can the caller see it" filter to apply, and none is silently missing.</para>
///
/// <para><b>Blocks apply</b> (privacy spec T0-3). Without this, blocking would still leak
/// co-membership through the mutual-servers field and would still let a blocked user's friend
/// request clear the ServerMembers policy - the block would stop the request at the friend-request
/// check and nowhere else, which is the "enforcement in some places is theatre" failure the spec
/// calls out. One cache read covers the whole batch, because the subject's block state lists both
/// directions.</para>
///
/// <para>Returns one response type, not a tuple - a tuple-returning Wolverine handler also publishes
/// the members it did not respond with.</para>
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

    /// <summary>One user's guild ids. Not exposed on any contract - it is the inner half of the
    /// intersection, and on its own it is exactly the roster this handler refuses to hand out.</summary>
    public static IQueryable<string> BuildMembershipQuery(MicroserviceContext ctx, string userId) =>
        ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.GuildId)
            .Distinct();

    /// <summary>The (user, guild) rows of <paramref name="otherUserIds"/> restricted to
    /// <paramref name="subjectGuildIds"/> - the intersection itself, in one query for the batch.
    ///
    /// <para>Built rather than inlined so the SQL can be checked against the real Npgsql provider:
    /// EF InMemory evaluates this in memory and would report success for a shape Postgres cannot
    /// translate.</para></summary>
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
