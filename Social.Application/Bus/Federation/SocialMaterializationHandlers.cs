using Echo.Realtime;
using Federation.Contracts.Materialization.Social;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Social.Api.Dtos.Realtime;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Bus.Federation;

/// <summary>
/// Materializes remote friend-request-lifecycle federation events. A remote user who has never
/// interacted with this instance before has no local Profile row at all - unlike GuildMember
/// (which hangs off an existing local Guild), a friendship needs a shadow Profile for the remote
/// party to even exist as an addressable entity, flagged via the existing (previously unused)
/// Profile.FederatedServerId field. FederatedServerId doubles as the marker Relationship rows
/// don't need their own copy of - a relationship is federated if either party's Profile is.
///
/// Idempotent by natural business key (Owner/Target profile pair), not by EventId - same
/// reasoning as GuildMaterializationHandlers.
/// </summary>
public class SocialMaterializationHandlers
{
    public static async Task Handle(FederatedFriendRequestReceived message, MicroserviceContext db,
        IHubContext<EchoRealtimeHub> hub, CancellationToken ct)
    {
        var remoteProfile = await GetOrCreateShadowProfileAsync(db, message.SenderId, message.OriginInstanceId, ct);
        var localProfile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == message.TargetUserId, ct);
        if (localProfile is null) return;

        // Also the block guard (privacy spec T0-3, "Federated inbound: drop at the inbox
        // boundary"): a block is a Relationship row owned by the local user and targeting the
        // remote shadow profile, so an inbound request from someone they blocked matches here and
        // is dropped - and dropped silently, which is what keeps the block invisible across the
        // federation boundary too.
        var alreadyExists = await db.Relationships.AnyAsync(
            r => r.OwnerId == localProfile.Id && r.TargetId == remoteProfile.Id, ct);
        if (alreadyExists) return;

        var relationships = Relationship.Create(new CreateRelationshipParams
        {
            Initiator = remoteProfile.Id,
            Subject = localProfile.Id,
        });

        // Relationship.Create() makes the initiator's own copy PendingOutgoing and the subject's
        // PendingIncoming - correct here since the remote party is the initiator. The outgoing
        // (remote-owned) side isn't meaningful to persist as its own queryable row on this
        // instance; only the local (incoming) side needs to exist locally.
        var incoming = relationships.First(r => r.OwnerId == localProfile.Id);
        db.Relationships.Add(incoming);
        await db.SaveChangesAsync(ct);

        await PushAsync(hub, incoming, localProfile, remoteProfile, "social.FriendRequestCreated");
    }

    public static Task Handle(FederatedFriendAcceptedReceived message, MicroserviceContext db,
        IHubContext<EchoRealtimeHub> hub, CancellationToken ct)
        => ApplyRemoteTransitionAsync(db, hub, message.SenderId, RelationshipStatus.Friends,
            "social.FriendRequestAccepted", ct);

    public static Task Handle(FederatedFriendRejectedReceived message, MicroserviceContext db,
        IHubContext<EchoRealtimeHub> hub, CancellationToken ct)
        => ApplyRemoteTransitionAsync(db, hub, message.SenderId, RelationshipStatus.None,
            "social.FriendRequestRejected", ct);

    public static Task Handle(FederatedFriendRemovedReceived message, MicroserviceContext db,
        IHubContext<EchoRealtimeHub> hub, CancellationToken ct)
        => ApplyRemoteTransitionAsync(db, hub, message.SenderId, RelationshipStatus.None,
            "social.FriendRemoved", ct);

    /// <summary>
    /// Applies a remote-driven status change to the local user's own relationship row and pushes
    /// the matching social.* event. The status is assigned directly rather than through
    /// Relationship.Accept()/Remove() on purpose: those raise domain events, which Federation
    /// turns back into outbound federation messages - i.e. it would echo the remote instance's own
    /// change straight back at it. Skips when the row is already in the target state so a
    /// redelivered federation message doesn't push twice.
    ///
    /// The transition is validated against the row's current status rather than assigned
    /// unconditionally: an "accepted" is only meaningful for a request the local user actually
    /// sent, so without this a remote instance could move a row the local user had explicitly
    /// rejected (None) straight to Friends and re-open DMs and calls the local user refused.
    /// </summary>
    private static async Task ApplyRemoteTransitionAsync(
        MicroserviceContext db, IHubContext<EchoRealtimeHub> hub, string senderId,
        RelationshipStatus status, string eventName, CancellationToken ct)
    {
        var found = await FindLocalSideAsync(db, senderId, ct);
        if (found is null) return;

        var (relationship, remoteProfile) = found.Value;
        if (relationship.Status == status) return;
        if (!IsLegalRemoteTransition(relationship.Status, status)) return;

        relationship.Status = status;
        await db.SaveChangesAsync(ct);

        await PushAsync(hub, relationship, relationship.Owner, remoteProfile, eventName);
    }

    /// <summary>Which status changes a remote instance is allowed to drive on a local row.</summary>
    private static bool IsLegalRemoteTransition(RelationshipStatus current, RelationshipStatus target) =>
        target switch
        {
            // The remote accepted a request this user sent. Only meaningful from PendingOutgoing -
            // never from None (already rejected/removed) and never from Blocked.
            RelationshipStatus.Friends => current == RelationshipStatus.PendingOutgoing,

            // The remote rejected a pending request or unfriended. Legal from any live state
            // except Blocked, which is the local user's decision and not the remote's to clear.
            RelationshipStatus.None => current is RelationshipStatus.PendingOutgoing
                or RelationshipStatus.PendingIncoming
                or RelationshipStatus.Friends,

            _ => false,
        };

    private static Task PushAsync(
        IHubContext<EchoRealtimeHub> hub, Relationship relationship, Profile localProfile,
        Profile remoteProfile, string eventName)
        => hub.Clients.User(localProfile.UserId).SendAsync(eventName, new FriendRelationshipPayload
        {
            RelationshipId = relationship.Id,
            Status = relationship.Status,
            UserId = remoteProfile.UserId,
            ProfileId = remoteProfile.Id,
            UserName = remoteProfile.UserName,
        });

    private static async Task<Profile> GetOrCreateShadowProfileAsync(
        MicroserviceContext db, string federatedUserId, string originInstanceId, CancellationToken ct)
    {
        var existing = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == federatedUserId, ct);
        if (existing is not null) return existing;

        // No display name is available in-band on these federation events - same limitation as
        // GuildMaterializationHandlers, same fix path (IFederationProvider.GetUserProfileAsync).
        var profile = Profile.Create(new CreateProfileParams
        {
            UserId = federatedUserId,
            Username = federatedUserId,
        });
        profile.FederatedServerId = originInstanceId;

        db.Profiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return profile;
    }

    private static async Task<(Relationship Relationship, Profile RemoteProfile)?> FindLocalSideAsync(
        MicroserviceContext db, string remoteFederatedId, CancellationToken ct)
    {
        var remoteProfile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == remoteFederatedId, ct);
        if (remoteProfile is null) return null;

        // The local side is the row *owned by the local user*, i.e. the one merely targeting the
        // remote shadow profile. Matching on OwnerId too (as this used to) is wrong for a locally
        // initiated request, where both mirrored rows exist locally and the remote-owned one could
        // win the FirstOrDefault - flipping the mirror while leaving the local user's own row
        // stuck pending, and leaving no local user to push to.
        var relationship = await db.Relationships
            .Include(r => r.Owner)
            .FirstOrDefaultAsync(r => r.TargetId == remoteProfile.Id, ct);

        return relationship is null ? null : (relationship, remoteProfile);
    }
}
