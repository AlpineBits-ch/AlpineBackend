using Microsoft.EntityFrameworkCore;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Services;

/// <summary>
/// The one place that answers "may these two reach each other" inside Social (privacy spec T0-3).
///
/// <para>A block is a single <c>Relationship</c> row owned by the blocker with
/// <see cref="RelationshipStatus.Blocked"/> and a null <c>RelatedId</c>. There is no mirrored row,
/// which is what makes the block invisible to the blocked party: every list the blocked user can
/// read is scoped to rows they own, and they own nothing here.</para>
///
/// <para>Static rather than an injected service so the Wolverine static handlers can use it without
/// changing their signatures, and so a caller cannot accidentally hold a stale snapshot of the
/// block graph - each method is a query against the ambient DbContext.</para>
/// </summary>
public static class BlockQueries
{
    /// <summary>Directed: did <paramref name="blockerProfileId"/> block
    /// <paramref name="blockedProfileId"/>?</summary>
    public static Task<bool> HasBlockedAsync(this MicroserviceContext ctx, string blockerProfileId, string blockedProfileId)
        => ctx.Relationships.AsNoTracking().AnyAsync(r =>
            r.OwnerId == blockerProfileId && r.TargetId == blockedProfileId && r.Status == RelationshipStatus.Blocked);

    /// <summary>
    /// Both directions in one round trip. Callers almost always need both legs - the refusal a
    /// blocked initiator gets has to be indistinguishable from an ordinary policy refusal, while the
    /// refusal the *blocker* gets may name the block - so asking twice is wasted work.
    /// </summary>
    public static async Task<(bool AblockedB, bool BblockedA)> BlockStateAsync(
        this MicroserviceContext ctx, string profileA, string profileB)
    {
        var rows = await ctx.Relationships.AsNoTracking()
            .Where(r => r.Status == RelationshipStatus.Blocked &&
                        ((r.OwnerId == profileA && r.TargetId == profileB) ||
                         (r.OwnerId == profileB && r.TargetId == profileA)))
            .Select(r => r.OwnerId)
            .ToListAsync();

        return (rows.Contains(profileA), rows.Contains(profileB));
    }

    /// <summary>True when a block exists in either direction - the test every push, projection and
    /// fan-out uses, since neither party should reach the other once one of them has blocked.</summary>
    public static async Task<bool> AnyBlockBetweenAsync(this MicroserviceContext ctx, string profileA, string profileB)
    {
        var (aBlockedB, bBlockedA) = await ctx.BlockStateAsync(profileA, profileB);
        return aBlockedB || bBlockedA;
    }

    /// <summary>
    /// Same question as <see cref="AnyBlockBetweenAsync"/> but keyed by Identity user ids, for the
    /// realtime and integration paths that never load a profile.
    /// </summary>
    public static Task<bool> AnyBlockBetweenUsersAsync(this MicroserviceContext ctx, string userA, string userB)
        => ctx.Relationships.AsNoTracking().AnyAsync(r =>
            r.Status == RelationshipStatus.Blocked &&
            ((r.Owner.UserId == userA && r.Target.UserId == userB) ||
             (r.Owner.UserId == userB && r.Target.UserId == userA)));

    /// <summary>
    /// Every profile id in <paramref name="otherProfileIds"/> that is on either side of a block with
    /// <paramref name="viewerProfileId"/>. One query for a whole list, so a member list or a batch
    /// profile lookup does not turn into N block checks.
    /// </summary>
    public static async Task<HashSet<string>> BlockedEitherWayAsync(
        this MicroserviceContext ctx, string viewerProfileId, IReadOnlyCollection<string> otherProfileIds)
    {
        if (otherProfileIds.Count == 0) return [];

        var rows = await ctx.Relationships.AsNoTracking()
            .Where(r => r.Status == RelationshipStatus.Blocked &&
                        ((r.OwnerId == viewerProfileId && otherProfileIds.Contains(r.TargetId)) ||
                         (r.TargetId == viewerProfileId && otherProfileIds.Contains(r.OwnerId))))
            .Select(r => new { r.OwnerId, r.TargetId })
            .ToListAsync();

        return rows
            .Select(r => r.OwnerId == viewerProfileId ? r.TargetId : r.OwnerId)
            .ToHashSet();
    }
}
