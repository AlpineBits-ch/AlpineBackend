using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Social.Api.Dtos.Realtime;
using Social.Api.Services;
using Social.Contracts.Bus.Integration.Events;
using Social.Infrastructure.Persistence;

namespace Social.Api.Integration.Relationship.Events;

/// <summary>
/// Websocket fan-out for the friend-request lifecycle. Before this, the only relationship push in
/// the whole system was Messaging's <c>conversation.FriendRequestAccepted</c> (initiator only) -
/// creating, rejecting and removing were silent, so a client only ever learned about an incoming
/// friend request by polling GET /api/v1/relationships.
///
/// Every event goes to *both* parties, each with their own row id and status (see
/// <see cref="FriendRelationshipPayload"/>): the counterpart needs it to update the list, and the
/// actor needs it to keep its other devices in sync.
///
/// These run off the integration events rather than the raw domain events so the push happens
/// after the transaction that produced them has committed - a client that reacts by re-fetching
/// can't observe pre-commit state. Idempotency is upstream: <c>Relationship.Accept/Reject/Remove</c>
/// only raise their domain event on a real transition, so a repeated accept never reaches here.
/// </summary>
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

    /// <summary>The one <c>social.*</c> event a blocked pair may still exchange. See
    /// <see cref="PushBothSidesAsync"/>.</summary>
    private const string FriendRemovedEventName = "social.FriendRemoved";

    /// <summary>
    /// Loads the mirrored pair the event's relationship id belongs to and pushes one
    /// recipient-oriented payload to each owner. The Related side is null for a federation-
    /// materialized relationship (only the local half of those is ever persisted), in which case
    /// only the local user is notified.
    ///
    /// <para><b>Blocking (privacy spec T0-3).</b> No <c>social.*</c> event flows between a pair with
    /// a block in either direction - with one deliberate exception: <c>social.FriendRemoved</c>. The
    /// act of blocking tears down the friendship, and the blocked party has to be told the
    /// friendship ended or their client keeps showing a friend they no longer have. That disclosure
    /// is exactly what the spec allows them to see ("B sees the same thing as not friends"); every
    /// other event would tell them something about a person who has cut them off.</para>
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
