using Microsoft.EntityFrameworkCore;
using Social.Api.Dtos.Response;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Services;

/// <summary>
/// Relationship-graph questions the privacy gates ask: how a viewer stands to a subject, and who
/// they have in common. Social owns the friendship graph outright, so both are local queries.
/// </summary>
public static class RelationshipQueries
{
    /// <summary>
    /// Classifies the viewer for <see cref="ProfileVisibility"/>. Blocking is checked before
    /// friendship because a block tears the friendship down anyway, and because a stale Friends row
    /// must never outrank a live block.
    /// </summary>
    public static async Task<ViewerRelation> ResolveViewerRelationAsync(
        this MicroserviceContext ctx, string viewerProfileId, string subjectProfileId)
    {
        if (viewerProfileId == subjectProfileId) return ViewerRelation.SelfView;

        if (await ctx.AnyBlockBetweenAsync(viewerProfileId, subjectProfileId))
            return ViewerRelation.Blocked;

        var friends = await ctx.Relationships.AsNoTracking().AnyAsync(r =>
            r.OwnerId == viewerProfileId && r.TargetId == subjectProfileId &&
            r.Status == RelationshipStatus.Friends);

        return friends ? ViewerRelation.Friend : ViewerRelation.Other;
    }

    /// <summary>True when the two profiles hold an accepted friendship (viewer's side).</summary>
    public static Task<bool> AreFriendsAsync(this MicroserviceContext ctx, string profileA, string profileB)
        => ctx.Relationships.AsNoTracking().AnyAsync(r =>
            r.OwnerId == profileA && r.TargetId == profileB && r.Status == RelationshipStatus.Friends);

    /// <summary>
    /// Friends the viewer and the subject have in common - the payload behind
    /// <c>MutualFriendsVisibility</c>, and the test behind <c>FriendRequestPolicy.FriendsOfFriends</c>.
    /// </summary>
    public static async Task<IReadOnlyList<MutualFriendDto>> MutualFriendsAsync(
        this MicroserviceContext ctx, string viewerProfileId, string subjectProfileId)
    {
        var viewerFriendIds = ctx.Relationships.AsNoTracking()
            .Where(r => r.OwnerId == viewerProfileId && r.Status == RelationshipStatus.Friends)
            .Select(r => r.TargetId);

        var rows = await ctx.Relationships.AsNoTracking()
            .Where(r => r.OwnerId == subjectProfileId &&
                        r.Status == RelationshipStatus.Friends &&
                        viewerFriendIds.Contains(r.TargetId))
            .Select(r => new { r.TargetId, r.Target.UserId, r.Target.UserName })
            .ToListAsync();

        return rows
            .Select(r => new MutualFriendDto { ProfileId = r.TargetId, UserId = r.UserId, UserName = r.UserName })
            .ToList();
    }

    /// <summary>Cheap existence form of <see cref="MutualFriendsAsync"/> for the
    /// <c>FriendsOfFriends</c> policy branch, which only needs "at least one".</summary>
    public static Task<bool> HasMutualFriendAsync(
        this MicroserviceContext ctx, string viewerProfileId, string subjectProfileId)
    {
        var viewerFriendIds = ctx.Relationships.AsNoTracking()
            .Where(r => r.OwnerId == viewerProfileId && r.Status == RelationshipStatus.Friends)
            .Select(r => r.TargetId);

        return ctx.Relationships.AsNoTracking().AnyAsync(r =>
            r.OwnerId == subjectProfileId &&
            r.Status == RelationshipStatus.Friends &&
            viewerFriendIds.Contains(r.TargetId));
    }

    /// <summary>Loads a profile by its Identity user id without tracking - the read path every
    /// projection uses, so a projection can never dirty the change tracker.</summary>
    public static Task<Profile?> ProfileByUserIdAsync(this MicroserviceContext ctx, string userId)
        => ctx.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
}
