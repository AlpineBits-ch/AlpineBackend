using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Events;
using Social.Domain.Events.Relationship;
using Social.Infrastructure.Persistence;

namespace Social.Api.Integration.Relationship.Events;

/// <summary>
/// Translates local Relationship domain events into cross-service contracts, same shape as
/// FriendshipAcceptedHandler - these three didn't have a cross-service equivalent yet
/// (FriendRequestCreated was actually raised with all-null fields until now; nothing had ever
/// consumed it to notice).
/// </summary>
public class FriendRequestLifecycleHandlers
{
    public static async Task<FriendRequestCreatedEvent> Handle(FriendRequestCreated created, MicroserviceContext ctx)
    {
        var (initiatorUserId, targetUserId) = await ResolveUserIdsAsync(ctx, created.InitiatorProfileId, created.TargetProfileId);
        return new FriendRequestCreatedEvent
        {
            InitiatorUserId = initiatorUserId,
            TargetUserId = targetUserId,
            RelationshipId = created.RelationshipId,
        };
    }

    public static async Task<FriendRequestRejectedEvent> Handle(FriendRequestRejected rejected, MicroserviceContext ctx)
    {
        var (initiatorUserId, targetUserId) = await ResolveUserIdsAsync(ctx, rejected.InitiatorProfileId, rejected.TargetProfileId);
        return new FriendRequestRejectedEvent
        {
            InitiatorUserId = initiatorUserId,
            TargetUserId = targetUserId,
            RelationshipId = rejected.RelationshipId,
        };
    }

    public static async Task<FriendRemovedEvent> Handle(FriendRemoved removed, MicroserviceContext ctx)
    {
        var (initiatorUserId, targetUserId) = await ResolveUserIdsAsync(ctx, removed.InitiatorProfileId, removed.TargetProfileId);
        return new FriendRemovedEvent
        {
            InitiatorUserId = initiatorUserId,
            TargetUserId = targetUserId,
            RelationshipId = removed.RelationshipId,
        };
    }

    private static async Task<(string InitiatorUserId, string TargetUserId)> ResolveUserIdsAsync(
        MicroserviceContext ctx, string initiatorProfileId, string targetProfileId)
    {
        var initiatorUserId = await ctx.Profiles.Where(p => p.Id == initiatorProfileId).Select(p => p.UserId).FirstAsync();
        var targetUserId = await ctx.Profiles.Where(p => p.Id == targetProfileId).Select(p => p.UserId).FirstAsync();
        return (initiatorUserId, targetUserId);
    }
}
