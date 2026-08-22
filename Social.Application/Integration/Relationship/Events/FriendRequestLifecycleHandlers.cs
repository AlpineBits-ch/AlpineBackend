using Microsoft.EntityFrameworkCore;
using Social.Api.Services;
using Social.Contracts.Bus.Integration.Events;
using Social.Domain.Events.Relationship;
using Social.Infrastructure.Persistence;
using Wolverine;

namespace Social.Api.Integration.Relationship.Events;

/// <summary>
/// Translates local Relationship domain events into cross-service contracts, same shape as
/// FriendshipAcceptedHandler - these three didn't have a cross-service equivalent yet
/// (FriendRequestCreated was actually raised with all-null fields until now; nothing had ever
/// consumed it to notice).
/// </summary>
public class FriendRequestLifecycleHandler
{
    public static async Task Handle(FriendRequestCreated created, MicroserviceContext ctx, IMessageBus bus)
    {
        var (initiatorUserId, targetUserId) = await ResolveUserIdsAsync(ctx, created.InitiatorProfileId, created.TargetProfileId);

        if (await ctx.AnyBlockBetweenAsync(created.InitiatorProfileId, created.TargetProfileId)) return;

        await bus.PublishAsync(new FriendRequestCreatedEvent
        {
            InitiatorUserId = initiatorUserId,
            TargetUserId = targetUserId,
            RelationshipId = created.RelationshipId,
        });
    }

    public static async Task Handle(FriendRequestRejected rejected, MicroserviceContext ctx, IMessageBus bus)
    {
        var (initiatorUserId, targetUserId) = await ResolveUserIdsAsync(ctx, rejected.InitiatorProfileId, rejected.TargetProfileId);

        if (await ctx.AnyBlockBetweenAsync(rejected.InitiatorProfileId, rejected.TargetProfileId)) return;

        await bus.PublishAsync(new FriendRequestRejectedEvent
        {
            InitiatorUserId = initiatorUserId,
            TargetUserId = targetUserId,
            RelationshipId = rejected.RelationshipId,
        });
    }

    /// <summary>Not gated on blocking - see the class remarks.</summary>
    public static async Task Handle(FriendRemoved removed, MicroserviceContext ctx, IMessageBus bus)
    {
        var (initiatorUserId, targetUserId) = await ResolveUserIdsAsync(ctx, removed.InitiatorProfileId, removed.TargetProfileId);

        await bus.PublishAsync(new FriendRemovedEvent
        {
            InitiatorUserId = initiatorUserId,
            TargetUserId = targetUserId,
            RelationshipId = removed.RelationshipId,
        });
    }

    private static async Task<(string InitiatorUserId, string TargetUserId)> ResolveUserIdsAsync(
        MicroserviceContext ctx, string initiatorProfileId, string targetProfileId)
    {
        var initiatorUserId = await ctx.Profiles.Where(p => p.Id == initiatorProfileId).Select(p => p.UserId).FirstAsync();
        var targetUserId = await ctx.Profiles.Where(p => p.Id == targetProfileId).Select(p => p.UserId).FirstAsync();
        return (initiatorUserId, targetUserId);
    }
}
