using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Social.Api.Dtos.Realtime;
using Social.Api.Services;
using Social.Contracts.Bus.Integration.Events;
using Social.Infrastructure.Persistence;

namespace Social.Api.Integration.Relationship.Events;

/// <summary>Websocket fan-out for the friend-request lifecycle.</summary>
public class RelationshipRealtimeHandlers
{
    public static Task Handle(FriendRequestCreatedEvent created, MicroserviceContext ctx, IHubContext<EchoRealtimeHub> hub)
        => PushBothSidesAsync(ctx, hub, created.RelationshipId, "social.FriendRequestCreated");

    public static Task Handle(FriendshipAcceptedEvent accepted, MicroserviceContext ctx, IHubContext<EchoRealtimeHub> hub)
        => PushBothSidesAsync(ctx, hub, accepted.FriendshipId, "social.FriendRequestAccepted");

    public static Task Handle(FriendRequestRejectedEvent rejected, MicroserviceContext ctx, IHubContext<EchoRealtimeHub> hub)
        => PushBothSidesAsync(ctx, hub, rejected.RelationshipId, "social.FriendRequestRejected");

    public static Task Handle(FriendRemovedEvent removed, MicroserviceContext ctx, IHubContext<EchoRealtimeHub> hub)
        => PushBothSidesAsync(ctx, hub, removed.RelationshipId, "social.FriendRemoved");

    /// <summary>The one <c>social.*</c> event a blocked pair may still exchange.</summary>
    private const string FriendRemovedEventName = "social.FriendRemoved";

    /// <summary>
    /// Loads the mirrored pair the event's relationship id belongs to and pushes one
    /// recipient-oriented payload to each owner.
    /// </summary>
    public static async Task PushBothSidesAsync(
        MicroserviceContext ctx, IHubContext<EchoRealtimeHub> hub, string relationshipId, string eventName)
    {
        var relationship = await ctx.Relationships
            .AsNoTracking()
            .Include(r => r.Owner)
            .Include(r => r.Target)
            .Include(r => r.Related).ThenInclude(r => r.Owner)
            .Include(r => r.Related).ThenInclude(r => r.Target)
            .FirstOrDefaultAsync(r => r.Id == relationshipId);

        // The row can legitimately be gone by the time this runs (account deletion purges
        // relationships, and the bus hop is asynchronous) - nothing left to tell anyone about.
        if (relationship is null) return;

        if (eventName != FriendRemovedEventName &&
            relationship.Owner is not null && relationship.Target is not null &&
            await ctx.AnyBlockBetweenAsync(relationship.OwnerId, relationship.TargetId))
            return;

        await PushSideAsync(hub, relationship, eventName);
        if (relationship.Related is not null)
            await PushSideAsync(hub, relationship.Related, eventName);
    }

    private static Task PushSideAsync(
        IHubContext<EchoRealtimeHub> hub, Domain.Aggregate.Relationship side, string eventName)
    {
        // Owner/Target are required FKs, but a shadow profile that was purged mid-flight would
        // leave them unloaded rather than throw - skip instead of NREing the whole handler.
        if (side.Owner is null || side.Target is null) return Task.CompletedTask;

        return hub.Clients.User(side.Owner.UserId).SendAsync(eventName, new FriendRelationshipPayload
        {
            RelationshipId = side.Id,
            Status = side.Status,
            UserId = side.Target.UserId,
            ProfileId = side.Target.Id,
            UserName = side.Target.UserName,
        });
    }
}
